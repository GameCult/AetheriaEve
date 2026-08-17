# Aetheria Eve Conformance Pack

Provider-owned static contract fixtures for Aetheria's interactive world
surfaces. The daemon owns world state, authored surfaces, assets, commands, and
receipts. Eve and its runtime repos consume the contracts; they do not own
copies.

`verify-eve-conformance-pack.mjs` resolves every repository witness and checks
advertised Aetheria schema IDs against the typed `[CultDocument]` declarations
in this repository. Passing it proves fixture/source agreement only. It does
not prove a running daemon, command execution, receipts, reconnect, or renderer
lowering; those require executable smoke witnesses.

