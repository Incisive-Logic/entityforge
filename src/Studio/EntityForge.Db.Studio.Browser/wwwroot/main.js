import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = runtime.getConfig();
document.getElementById('out').replaceChildren();
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
