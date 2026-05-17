# CodeMemory Known Test Environment Issues

- In this environment, `dotnet test --no-build` for `E:\khurram-uworx\CodeMemory` can fail in ASP.NET/MCP tests because Windows EventLog logging cannot write source `.NET Runtime`.
- Failure signature: `System.InvalidOperationException: Cannot open log for source '.NET Runtime'. You may not have write access.` with inner `System.ComponentModel.Win32Exception (5): Access is denied.`
- Treat this as a known local environment/permissions issue unless the task is specifically about ASP.NET logging or EventLog configuration.
- Recent affected tests observed: `GetHotspots_ReturnsEmpty_WhenNoService`, `GetSymbolHistory_ReturnsWarning_WhenNoService`, and `ImpactAnalysis_ReturnsWarning_WhenNoGraphService`.
