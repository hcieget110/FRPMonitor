# Architecture

The Windows desktop application uses .NET / WPF. The floating window is one window containing two visual sections. The collectors remain independent.

| Responsibility | Local FRP | Cloud host |
|---|---|---|
| Collection | ETW through `MonitoringService` | Persistent SSH through `CloudMonitor` |
| Scope | Configured local processes / connection filters | Default-route network interface counters on Linux |
| History | Raw samples in memory, seven days of minute buckets on disk | `CloudHistory`, at most 300 seconds in memory |
| Credentials | Existing Windows startup task | `CloudProfile` metadata, `CloudCredential` Windows vault |
| Presentation | Existing main and floating FRP views | Reusable `CloudTrafficPanel` |
| Export policy | Up to five minutes: raw seconds; longer: minutes and recorded extrema | One or five minutes: seconds only |

`Exporting/SecondSeries` maps a snapshot to second slots. `SecondTrafficExporter` writes native editable Excel charts or CSV without knowing where traffic came from. `CloudExporter` and `ExportService` own their respective range and source selection. This avoids cross-module dependencies and duplicate workbook implementations.

Windows only orchestrate collection services, snapshot delivery, settings, and user actions. Network and workbook generation run in background tasks. Cloud connection failures leave missing intervals and do not affect local ETW collection. The cloud service reconnects automatically; disabling or reconfiguring it stops only its own SSH worker.

Linux sampling requires an ordinary SSH account and `python3`. It reads kernel counters, uses monotonic elapsed time, and leaves no installed service or script. Upload means bytes transmitted by the cloud host, and download means bytes received by the cloud host. SSH monitoring traffic is also included in its interface counters. Cloud samples are discarded on application exit.

FRP seconds are available only for samples retained during this application session. Persisted minute averages cannot recover individual seconds. Short exports leave those unavailable slots empty and do not pretend they were zero traffic.
