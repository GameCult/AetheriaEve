using GameCult.Eve.Surface;
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using GameCult.Networking;
using R3;
using EveSurfaceCommandRequest = GameCult.Eve.Surface.EveSurfaceCommandRequest;
using EveCommandReceiptDocument = GameCult.Eve.Surface.EveCommandReceiptDocument;

namespace Aetheria.State;

public enum AetheriaStateHydrationProfile
{
    All,
    DaemonBoot
}

/// <summary>
/// Read-only, snapshotting view of the Aetheria cache. Mutable cache handles remain private
/// to the state node so callers cannot bypass its transaction boundary.
/// </summary>
public sealed class AetheriaStateReadCache
{
    private readonly AetheriaStateNode _owner;

    internal AetheriaStateReadCache(AetheriaStateNode owner) => _owner = owner;

    public T? Get<T>(CultRecordKey key) where T : class =>
        _owner.CloneDocument(_owner.RawCache.Get<T>(key));

    public IReadOnlyList<T> GetAll<T>() where T : class =>
        _owner.RawCache.GetAll<T>()
            .Select(document => _owner.CloneDocument(document)!)
            .ToArray();

    public IReadOnlyList<object> AllEntries =>
        _owner.RawCache.AllStoredDocuments
            .Select(stored => _owner.CloneDocumentUntyped(stored.Document, stored.Descriptor.DocumentType))
            .ToArray();

    public IReadOnlyList<CultStoredDocument> GetStoredDocuments<T>() where T : class =>
        _owner.RawCache.GetStoredDocuments<T>()
            .Select(stored => new CultStoredDocument(
                stored.Key,
                stored.StoredAt,
                stored.Descriptor,
                _owner.CloneDocument((T)stored.Document)!))
            .ToArray();

    public bool Contains(CultRecordKey key) => _owner.RawCache.Get(key) != null;
}

public sealed class AetheriaStateNode : IAsyncDisposable, IDisposable
{
    private static readonly string[] DaemonNativeCatalogBootRecordKeys =
    [
        AetheriaCatalogKeys.ItemDefinitionFromLegacyId("82efc0a5-1ba5-4ff3-a281-b2e6e247521d").ToString(), // Zenith hull
        AetheriaCatalogKeys.ItemDefinitionFromLegacyId("3e930a2c-ac72-4385-98aa-1c5b0b90db46").ToString(), // Medium docking bay
        AetheriaCatalogKeys.ItemDefinitionFromLegacyId("ca098005-8cc8-47f4-be99-7bc842805359").ToString(), // Dockyard hull
        AetheriaCatalogKeys.ItemDefinitionFromLegacyId("8ec30f8d-8536-48b4-bd64-65f29f229895").ToString()  // Dockyard berth
    ];
    private readonly CultMeshNode _node;
    private readonly AetheriaStateReadCache _readCache;
    private CultMeshDocumentHandle<AetheriaRuntimeCatalogSnapshot>? _runtimeCatalog;
    private CultMeshDocumentHandle<AetheriaRuntimeNameCorpusSnapshot>? _runtimeNameCorpus;
    private CultMeshDocumentHandle<EveSurfaceDocument>? _catalogSurface;

    private AetheriaStateNode(string statePath, string runtimeId, CultMeshNode node)
    {
        StatePath = statePath;
        RuntimeId = string.IsNullOrWhiteSpace(runtimeId)
            ? "aetheria-local"
            : runtimeId;
        _node = node;
        _readCache = new AetheriaStateReadCache(this);
    }

    public string StatePath { get; }

    public string RuntimeId { get; }

    public CultMeshNode MeshNode => _node;

    public AetheriaStateReadCache Cache => _readCache;

    internal CultCache RawCache => _node.Cache;

    public CultNetDatabase Database => _node.Database;

    public CultNetSnapshotResponseRawMessage CreateRawSnapshotResponse(
        string messageId,
        CultNetSnapshotRequestMessage request) =>
        Database.Documents.CreateRawSnapshotResponse(RawCache, messageId, request);

    public Task HydrateRecordsAsync(Func<CultPersistedRecordMetadata, bool> selector) =>
        RawCache.PullBackingStoreRecordsAsync(selector);

    public static async Task<AetheriaStateNode> OpenAsync(
        string statePath,
        string runtimeId = "aetheria-local",
        bool startServer = false,
        bool enableDurableShardLogs = true,
        bool useDirectoryStore = true,
        AetheriaStateHydrationProfile hydrationProfile = AetheriaStateHydrationProfile.All)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            throw new ArgumentException("State path must be non-empty.", nameof(statePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? ".");

        var traceStartup = string.Equals(
            Environment.GetEnvironmentVariable("AETHERIA_TRACE_STARTUP_PHASES"),
            "1",
            StringComparison.Ordinal);
        var startupPhase = Stopwatch.StartNew();
        void Trace(string phase)
        {
            if (traceStartup)
                Console.WriteLine($"Aetheria state-node phase {phase} took {startupPhase.Elapsed.TotalMilliseconds:0.###}ms.");
            startupPhase.Restart();
        }

        var cacheRegistry = AetheriaDocumentRegistry.CreateCultCacheRegistry();
        Trace("cache-registry");
        var hydrationFilter = CreateHydrationFilter(hydrationProfile, cacheRegistry);
        Trace("hydration-filter");
        var databaseRegistry = AetheriaDocumentRegistry.CreateCultNetRegistry(cacheRegistry);
        Trace("database-registry");
        var node = await CultMesh.CreateNodeAsync(
            statePath,
            new CultMeshNodeOptions
            {
                StartServer = startServer,
                EnableDurableShardLogs = enableDurableShardLogs,
                CacheOptions = new CultCacheOpenOptions
                {
                    Registry = cacheRegistry,
                    PullOnOpen = true,
                    StoreFlushOnDispose = true,
                    UseDirectoryStore = useDirectoryStore,
                    DirectoryStoreHydrationFilter = hydrationFilter
                },
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    RuntimeId = runtimeId,
                    DocumentRegistry = databaseRegistry,
                    RequireTransactionsForAuthoritativeWrites = true
                }
            }).ConfigureAwait(false);
        Trace("cultmesh-node");

        return new AetheriaStateNode(statePath, runtimeId, node);
    }

    private static Func<CultPersistedRecordMetadata, bool>? CreateHydrationFilter(
        AetheriaStateHydrationProfile profile,
        CultDocumentRegistry registry)
    {
        if (profile == AetheriaStateHydrationProfile.All)
            return null;
        if (profile != AetheriaStateHydrationProfile.DaemonBoot)
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown Aetheria state hydration profile.");

        var schemaTypes = new[]
        {
            typeof(AetheriaItemDefinition),
            typeof(AetheriaCorporation),
            typeof(AetheriaNameFile),
            typeof(AetheriaLoadoutTemplate),
            typeof(AetheriaRuntimeAuthorityLeaseDocument),
            typeof(AetheriaRuntimeArenaRosterDocument),
            typeof(AetheriaRuntimeDaemonCommandDocument),
            typeof(AetheriaRuntimeCommittedCommandFactDocument),
            typeof(AetheriaRuntimeEveCommandDocument),
            typeof(EveSurfaceCommandRequest),
            typeof(EveCommandReceiptDocument)
        };
        var schemaIds = schemaTypes
            .Select(registry.GetRequired)
            .SelectMany(descriptor => descriptor.ToCatalogEntry().CompatibleSchemaIds.Append(descriptor.SchemaId))
            .ToHashSet(StringComparer.Ordinal);
        var recordKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            WorldKey.ToString(),
            RuntimeCatalogKey.ToString(),
            RuntimeNameCorpusKey.ToString(),
            TradeValuePolicyKey.ToString(),
            PlayerSettingsKey.ToString(),
            HangarKey.ToString(),
            HangarDraftKey.ToString(),
            AetheriaRuntimeVerseRecordKeys.HangarProjection.ToString(),
            ProgressionSourceKey.ToString(),
            VerseHostSettingsKey.ToString(),
            GameSessionStateKey.ToString(),
            MainMenuStateKey.ToString(),
            EveCommandAcceptanceStatusKey.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonProviderAdvertisement.ToString(),
            AetheriaRuntimeVerseRecordKeys.EveProviderAdvertisement.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest.ToString(),
            AetheriaRuntimeVerseRecordKeys.EveAssetCatalog.ToString(),
            AetheriaRuntimeVerseRecordKeys.PilotInputCapability.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonGameSurface.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonGameReactiveSurface.ToString(),
            AetheriaRuntimeVerseRecordKeys.MapMenuSurface.ToString(),
            AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy.ToString(),
            AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest.ToString()
        };
        recordKeys.UnionWith(DaemonNativeCatalogBootRecordKeys);
        return record => recordKeys.Contains(record.Key) || schemaIds.Contains(record.SchemaId);
    }

    public CultMeshDocumentHandle<TDocument> Document<TDocument>(CultRecordKey key)
        where TDocument : class
    {
        return CultMesh.Document<TDocument>(
            key.ToString(),
            CultMesh.Verse("aetheria.local", RuntimeId),
            async _ => (await Database.GetAsync<TDocument>(key).ConfigureAwait(false))!,
            _ => Database.WatchRecord<TDocument>(key)
                .Where(change => change.Document != null)
                .Select(change => change.Document!),
            sources: new[]
            {
                CultMesh.ProjectionSource(key.ToString())
            },
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "Aetheria typed state node document"));
    }

    public CultMeshMutableStatePointer<TDocument> MutableDocument<TDocument>(CultRecordKey key)
        where TDocument : class
    {
        return MutableDocumentPointer<TDocument>(key);
    }

    public IReadOnlyList<TDocument> Documents<TDocument>()
        where TDocument : class
    {
        return Cache.GetAll<TDocument>().ToArray();
    }

    public CultMeshDocumentHandle<AetheriaRuntimeCatalogSnapshot> RuntimeCatalog()
    {
        return _runtimeCatalog ??= CultMesh.Document(
            "aetheria.catalog.runtime",
            CultMesh.Verse("aetheria.local", RuntimeId),
            async _ => await Database.GetAsync<AetheriaRuntimeCatalogSnapshot>(RuntimeCatalogKey)
                    .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The compiled Aetheria runtime catalog is missing. Refresh it after changing source catalog records."),
            _ => Database.WatchRecord<AetheriaRuntimeCatalogSnapshot>(RuntimeCatalogKey)
                .Where(change => change.Document != null)
                .Select(change => change.Document!),
            sources: new[]
            {
                CultMesh.ProjectionSource("catalog:aetheria.runtime"),
                CultMesh.ProjectionSource(
                    "aetheria.catalog.runtime",
                    AetheriaRuntimeCatalogSnapshot.SchemaId,
                    "managed Aetheria runtime catalog document")
            },
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "Aetheria typed catalog state"));
    }

    public CultMeshDocumentHandle<AetheriaRuntimeNameCorpusSnapshot> RuntimeNameCorpus()
    {
        return _runtimeNameCorpus ??= CultMesh.Document(
            "aetheria.catalog.runtime-names",
            CultMesh.Verse("aetheria.local", RuntimeId),
            async _ => await Database.GetAsync<AetheriaRuntimeNameCorpusSnapshot>(RuntimeNameCorpusKey)
                    .ConfigureAwait(false)
                ?? throw new InvalidDataException("The compiled Aetheria runtime name corpus is missing."),
            _ => Database.WatchRecord<AetheriaRuntimeNameCorpusSnapshot>(RuntimeNameCorpusKey)
                .Where(change => change.Document != null)
                .Select(change => change.Document!),
            sources: new[]
            {
                CultMesh.ProjectionSource(
                    RuntimeNameCorpusKey.ToString(),
                    AetheriaRuntimeNameCorpusSnapshot.SchemaId,
                    "managed Aetheria runtime name corpus")
            },
            routeHint: new CultMeshRouteHint(
                CultMeshLocalityKind.SharedMemory,
                "Aetheria typed name generation corpus"));
    }

    public async Task<AetheriaRuntimeCatalogSnapshot> RuntimeCatalogForGenerationAsync()
    {
        var catalog = RuntimeCatalog().Latest()
            ?? throw new InvalidDataException("The compiled Aetheria runtime catalog is missing.");
        var corpus = Cache.Get<AetheriaRuntimeNameCorpusSnapshot>(RuntimeNameCorpusKey)
            ?? throw new InvalidDataException(
                "The compiled Aetheria runtime name corpus was not hydrated at daemon boot.");
        return new AetheriaRuntimeCatalogSnapshot(
            catalog.Items.ToArray(),
            catalog.Corporations.ToArray(),
            corpus.NameFiles.ToArray(),
            catalog.TradeValueSettings)
        {
            CatalogId = catalog.CatalogId,
            NameCorpusRecordKey = RuntimeNameCorpusKey.ToString()
        };
    }

    public CultMeshDocumentHandle<EveSurfaceDocument> CatalogSurface()
    {
        var catalog = RuntimeCatalog();
        return _catalogSurface ??= CultMesh.Document(
            "aetheria.catalog.surface",
            CultMesh.Verse("aetheria.local", RuntimeId),
            _ => Task.FromResult(AetheriaEveSurfaceDocuments.BuildCatalogSurface(
                catalog.Latest(),
                DateTimeOffset.UtcNow.ToString("O"))),
            _ => catalog.Watch()
                .Select(snapshot => AetheriaEveSurfaceDocuments.BuildCatalogSurface(
                    snapshot,
                    DateTimeOffset.UtcNow.ToString("O"))),
            sources: new[]
            {
                CultMesh.ProjectionSource(
                    "aetheria.catalog.runtime",
                    AetheriaRuntimeCatalogSnapshot.SchemaId,
                    "managed Aetheria runtime catalog document"),
                CultMesh.ProjectionSource(
                    CatalogSurfaceKey.ToString(),
                    "gamecult.eve.surface.v1",
                    "managed Aetheria catalog Eve surface document")
            },
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "Aetheria typed catalog Eve surface"));
    }

    public async Task<AetheriaRuntimeCatalogSnapshot> RefreshRuntimeCatalogAsync()
    {
        byte[] Encode<T>(T document) where T : class =>
            CultDocumentMessagePackSerialization.SerializeUntyped(
                document,
                typeof(T),
                RawCache.Registry);
        var tradePolicy = RawCache.Get<AetheriaTradeValuePolicy>(TradeValuePolicyKey);
        var snapshot = AetheriaRuntimeCatalogStore.FromDocumentPayloads(
            RawCache.GetAll<AetheriaItemDefinition>().Select(Encode),
            RawCache.GetAll<AetheriaCorporation>().Select(Encode),
            RawCache.GetAll<AetheriaNameFile>().Select(Encode),
            tradePolicy == null ? null : Encode(tradePolicy));
        return await CommitAsync(async () =>
        {
            await Database.PutAsync(
                RuntimeNameCorpusKey,
                new AetheriaRuntimeNameCorpusSnapshot
                {
                    NameFiles = snapshot.NameFiles.ToArray()
                }).ConfigureAwait(false);
            snapshot.NameFiles = snapshot.NameFiles
                .Select(nameFile => new AetheriaRuntimeNameFile(
                    nameFile.NameFileKey,
                    nameFile.Name,
                    nameFile.NameCount,
                    nameFile.SampleNames.ToArray(),
                    Array.Empty<string>()))
                .ToArray();
            snapshot.NameCorpusRecordKey = RuntimeNameCorpusKey.ToString();
            await Database.PutAsync(RuntimeCatalogKey, snapshot).ConfigureAwait(false);
            return snapshot;
        }).ConfigureAwait(false);
    }

    public Task<CultRecordHandle<AetheriaRuntimeDaemonCommandDocument>> SubmitDaemonCommandAsync(
        AetheriaRuntimeDaemonCommandDocument command)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        command.Schema = AetheriaRuntimeDaemonSchemas.Command;
        if (string.IsNullOrWhiteSpace(command.CommandId))
            command.CommandId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(command.IssuedAtUtc))
            command.IssuedAtUtc = DateTime.UtcNow.ToString("O");

        return CommitAsync(() => Database.PutAsync(DaemonCommandKey(command.CommandId), command));
    }

    public Task<CultRecordHandle<AetheriaRuntimeCommittedCommandFactDocument>> PutCommittedCommandFactAsync(
        AetheriaRuntimeCommittedCommandFactDocument fact)
    {
        if (fact == null) throw new ArgumentNullException(nameof(fact));
        fact.Schema = AetheriaRuntimeDaemonSchemas.CommittedCommandFact;
        if (string.IsNullOrWhiteSpace(fact.FactId))
            fact.FactId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(fact.CommittedAtUtc))
            fact.CommittedAtUtc = DateTime.UtcNow.ToString("O");

        return CommitAsync(() => Database.PutAsync(
            new CultRecordKey(AetheriaRuntimeCommittedCommandFactDocument.CreateRecordKey(fact.FactId)),
            fact));
    }

    public Task DeleteDaemonCommandAsync(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("Daemon command id must be non-empty.", nameof(commandId));

        return CommitAsync(() => Database.DeleteAsync<AetheriaRuntimeDaemonCommandDocument>(
            AetheriaRuntimeVerseRecordKeys.DaemonCommand(commandId)));
    }

    private static CultRecordKey DaemonCommandKey(string commandId)
    {
        return new CultRecordKey($"daemon:commands:{StableToken(commandId)}:gamecult.aetheria.daemon_command.v1");
    }

    public Task<CultRecordHandle<AetheriaRuntimeEveCommandDocument>> SubmitEveCommandAsync(
        AetheriaRuntimeEveCommandDocument command)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        AetheriaRuntimeEveCommandClient.NormalizeDocument(command);
        command.Schema = AetheriaRuntimeEveCommandDocument.SchemaId;
        if (string.IsNullOrWhiteSpace(command.CommandId))
            command.CommandId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(command.IssuedAtUtc))
            command.IssuedAtUtc = DateTime.UtcNow.ToString("O");

        return CommitAsync(() => Database.PutAsync(EveCommandKey(command.CommandId), command));
    }

    private static CultRecordKey EveCommandKey(string commandId)
    {
        return new CultRecordKey($"eve:commands:{StableToken(commandId)}:gamecult.eve.command.v1");
    }

    /// <summary>
    /// Owns one Aetheria state mutation and its durable manifest commit. Every document
    /// belonging to the mutation is staged while this scope is held; nested scopes join
    /// the outer commit and cannot publish an intermediate generation.
    /// </summary>
    public Task CommitAsync(Func<Task> stageAsync, bool soft = false)
    {
        if (stageAsync == null) throw new ArgumentNullException(nameof(stageAsync));
        return CommitAsync(async () =>
        {
            await stageAsync().ConfigureAwait(false);
            return true;
        }, soft);
    }

    /// <summary>Runs one value-producing mutation inside the state-node commit boundary.</summary>
    public Task<T> CommitAsync<T>(Func<Task<T>> stageAsync, bool soft = false)
    {
        if (stageAsync == null) throw new ArgumentNullException(nameof(stageAsync));
        return Database.ExecuteTransactionAsync(stageAsync, soft);
    }

    /// <summary>Flushes legacy state staged outside the transaction API.</summary>
    public Task FlushAsync(bool soft = false)
    {
        return _node.FlushAsync(soft);
    }

    private static string StableToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "empty";

        var chars = value
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var token = new string(chars).Trim('-').ToLowerInvariant();
        while (token.Contains("--", StringComparison.Ordinal))
            token = token.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(token) ? "empty" : token;
    }

    public static CultRecordKey RuntimeSessionKey(string runtimeId)
    {
        return new CultRecordKey($"runtime:{runtimeId}:aetheria.runtime_session.v1");
    }

    public static CultRecordKey WorldKey { get; } =
        new("global:aetheria.world_state.v1");

    public static CultRecordKey MigrationLedgerKey { get; } =
        new("global:aetheria.migration_ledger.v1");

    public static CultRecordKey LegacyCatalogQuarantineKey { get; } =
        new("global:aetheria.legacy_catalog_quarantine.v1");

    public static CultRecordKey CatalogSurfaceKey { get; } =
        new(AetheriaEveSurfaceDocuments.CatalogSurfaceKey);

    public static CultRecordKey RuntimeCatalogKey { get; } =
        new("global:aetheria.runtime_catalog.v1");

    public static CultRecordKey RuntimeNameCorpusKey { get; } =
        new("global:aetheria.runtime_name_corpus.v1");

    public static CultRecordKey OperationsSurfaceKey { get; } =
        new(AetheriaEveSurfaceDocuments.OperationsSurfaceKey);

    public static CultRecordKey PlayerSettingsSurfaceKey { get; } =
        new(AetheriaEveSurfaceDocuments.PlayerSettingsSurfaceKey);

    public static CultRecordKey ProviderAdvertisementSurfaceKey { get; } =
        new(AetheriaEveSurfaceDocuments.ProviderAdvertisementKey);

    public static CultRecordKey PlayerSettingsKey { get; } =
        new("global:aetheria.player_settings.v1");

    public static CultRecordKey HangarKey { get; } =
        new("global:gamecult.aetheria.hangar.v1");

    public static CultRecordKey HangarDraftKey { get; } =
        AetheriaRuntimeVerseRecordKeys.HangarDraft;

    public static CultRecordKey ProgressionSourceKey { get; } =
        new(AetheriaProgressionSourceDocument.DocumentKey);

    public static CultRecordKey VerseHostSettingsKey { get; } =
        new("global:aetheria.verse_host_settings.v1");

    public static CultRecordKey EveCommandAcceptanceStatusKey { get; } =
        new("global:aetheria.eve_command_acceptance_status.v1");

    public static CultRecordKey MainMenuStateKey { get; } =
        new("global:aetheria.main_menu_state.v1");

    public static CultRecordKey GameSessionStateKey { get; } =
        new("global:aetheria.game_session.v1");

    public static CultRecordKey TradeValuePolicyKey { get; } =
        new(AetheriaTradeValuePolicy.RecordKey);

    private CultMeshMutableStatePointer<T> MutableDocumentPointer<T>(CultRecordKey key) where T : class
    {
        return CultMesh.MutableStatePointer(
            key.ToString(),
            async _ => CloneDocument(await Database.GetAsync<T>(key).ConfigureAwait(false)),
            _ => Database.WatchRecord<T>(key)
                .Where(change => change.Document != null)
                .Select(change => CloneDocument(change.Document)!),
            async (_, value) =>
            {
                await CommitAsync(async () =>
                {
                    await Database.PutAsync(key, value).ConfigureAwait(false);
                }).ConfigureAwait(false);
            },
            sources:
            [
                CultMesh.ProjectionSource(key.ToString())
            ]);
    }

    public void Dispose()
    {
        _node.Dispose();
    }

    internal T? CloneDocument<T>(T? document) where T : class
    {
        if (document == null)
            return null;
        var payload = CultDocumentMessagePackSerialization.SerializeUntyped(
            document,
            typeof(T),
            RawCache.Registry);
        return (T)CultDocumentMessagePackSerialization.DeserializeUntyped(
            typeof(T),
            payload,
            RawCache.Registry);
    }

    internal object CloneDocumentUntyped(object document, Type documentType)
    {
        var payload = CultDocumentMessagePackSerialization.SerializeUntyped(
            document,
            documentType,
            RawCache.Registry);
        return CultDocumentMessagePackSerialization.DeserializeUntyped(
            documentType,
            payload,
            RawCache.Registry);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        Dispose();
    }
}
