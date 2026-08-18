import { parseEveSurfaceDocument } from "@gamecult/eve-contracts";
import { renderEveSurface } from "@gamecult/eve-browser-lowering";
import {
  CultMeshBrowserClient,
  CultMeshBrowserOdinRendezvous,
  decodeCultNetPayload,
} from "cultmesh-browser";

declare global {
  interface Window {
    __aetheriaWitness?: {
      title: string;
      verseOptions: string[];
      buttons: string[];
      connectionStates: string[];
      commandId: string;
      commandStatus: string;
      receiptSchema: string;
      forgedIdentityStatus: string;
    };
    __aetheriaIssueVerseSelection?: () => Promise<{
      commandId: string;
      commandStatus: string;
      receiptSchema: string;
    }>;
    __aetheriaWitnessError?: string;
  }
}

const parameters = new URLSearchParams(location.search);
const endpoint = parameters.get("endpoint");
if (!endpoint) throw new Error("The Aetheria browser witness requires an endpoint.");
const token = parameters.get("token");
if (token) document.cookie = `cultnet_session=${encodeURIComponent(token)}; Path=/; SameSite=Strict`;
const host = document.querySelector<HTMLElement>("#surface");
if (!host) throw new Error("The Aetheria browser witness host is missing.");

try {
  const mesh = await CultMeshBrowserClient.connect({
    verseId: "aetheria.local",
    providerId: "aetheria-daemon",
    runtimeId: "aetheria.browser-witness",
    rendezvous: new CultMeshBrowserOdinRendezvous({
      endpoints: [endpoint],
      runtimeId: "aetheria.browser-witness.discovery",
    }),
  });
  const connectionStates: string[] = [];
  mesh.watchState(state => connectionStates.push(state));
  const lease = await mesh.leaseRawDocument({
    schemaId: "gamecult.eve.surface.v1",
    recordKey: "eve:surface:aetheria.hangar",
    subscriptionId: "aetheria-browser-hangar",
  });
  if (!lease.current) throw new Error("The Aetheria daemon returned no Hangar surface.");
  const surface = parseEveSurfaceDocument(decodeCultNetPayload(lease.current));
  let commandProof: Promise<{ commandId: string; commandStatus: string; receiptSchema: string }> | undefined;
  let submittedIntent: import("@gamecult/eve-browser-lowering").EveCommandIntent | undefined;
  renderEveSurface(surface, host, {
    activeSurfaceId: "aetheria.hangar",
    clientId: "aetheria.browser-witness",
    stateBindingResolver: async () => undefined,
    documentResolver: async () => undefined,
    commandSink: intent => {
      submittedIntent = intent;
      commandProof = submitAndObserveReceipt(mesh, intent);
      return commandProof.then(() => undefined);
    },
  });
  await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
  const verseSelect = host.querySelector<HTMLSelectElement>("select");
  if (!verseSelect) throw new Error("The Hangar Verse selector did not lower as an interactive select.");
  const issueVerseSelection = async () => {
    commandProof = undefined;
    verseSelect.value = "local";
    verseSelect.dispatchEvent(new Event("change", { bubbles: true }));
    const submitted = commandProof;
    if (!submitted) throw new Error("Changing the Hangar Verse selector emitted no Eve command.");
    return await submitted;
  };
  window.__aetheriaIssueVerseSelection = issueVerseSelection;
  const command = await issueVerseSelection();
  if (!submittedIntent) throw new Error("The Hangar Verse selector command lost its typed intent.");
  const forgedIdentityStatus = await verifyForgedClientDenied(mesh, submittedIntent);

  window.__aetheriaWitness = {
    title: surface.title,
    verseOptions: [...host.querySelectorAll("select option")].map(option => option.textContent?.trim() || ""),
    buttons: [...host.querySelectorAll("button")].map(button => button.textContent?.trim() || ""),
    connectionStates,
    ...command,
    forgedIdentityStatus,
  };
} catch (error) {
  window.__aetheriaWitnessError = error instanceof Error ? error.stack || error.message : String(error);
  host.textContent = window.__aetheriaWitnessError;
  throw error;
}

async function verifyForgedClientDenied(
  mesh: CultMeshBrowserClient,
  intent: import("@gamecult/eve-browser-lowering").EveCommandIntent,
): Promise<string> {
  const response = await mesh.invoke({
    serviceId: intent.commandBoundary || "aetheria.daemon.commands",
    operation: intent.command,
    payloadSchema: intent.schema,
    payload: { ...intent, clientId: "aetheria.forged-browser" },
  });
  if (response.status !== "denied" ||
      !response.diagnostics?.some(diagnostic => diagnostic.includes("identity"))) {
    throw new Error(`Forged Eve client identity was not denied: ${JSON.stringify(response)}`);
  }
  return response.status;
}

async function submitAndObserveReceipt(
  mesh: CultMeshBrowserClient,
  intent: import("@gamecult/eve-browser-lowering").EveCommandIntent,
): Promise<{ commandId: string; commandStatus: string; receiptSchema: string }> {
  const response = await mesh.invoke({
    serviceId: intent.commandBoundary || "aetheria.daemon.commands",
    operation: intent.command,
    payloadSchema: intent.schema,
    payload: intent,
  });
  if (response.status !== "queued" && response.status !== "accepted") {
    throw new Error(`Aetheria daemon denied browser command: ${response.diagnostics?.join("; ") || response.status}`);
  }
  const receiptLease = await mesh.leaseRawDocument({
    schemaId: "gamecult.eve.command_receipt.v1",
    recordKey: `eve:receipts:aetheria.daemon:${response.messageId}`,
    subscriptionId: `aetheria-browser-receipt-${response.messageId}`,
  });
  try {
    const receipt = receiptLease.current || await new Promise<NonNullable<typeof receiptLease.current>>((resolve, reject) => {
      const timer = setTimeout(() => {
        unwatch();
        reject(new Error(`Timed out waiting for Aetheria receipt '${response.messageId}'.`));
      }, 10_000);
      const unwatch = receiptLease.watch(record => {
        if (!record) return;
        clearTimeout(timer);
        unwatch();
        resolve(record);
      });
    });
    return {
      commandId: response.messageId,
      commandStatus: response.status,
      receiptSchema: receipt.schemaVersion || receipt.schemaId,
    };
  } finally {
    receiptLease.dispose();
  }
}
