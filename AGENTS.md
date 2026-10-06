# Development rules

Keep local FRP monitoring and cloud host monitoring independent when adding or changing features.

- Local FRP owns ETW collection, process filters, persistent minute history, and its export selection. Cloud code must not change or restart that collector.
- `Cloud/` owns SSH collection, connection settings, credentials, the bounded five-minute buffer, cloud status, controls, and export selection. Local FRP code must not import cloud business services.
- `MainWindow` and `FloatWindow` compose the modules and route user actions. Do not put network protocols, aggregation, credentials, or workbook XML in windows.
- Share only generic presentation and pure utilities. `Exporting/` takes immutable snapshots and has no dependency on collectors, cloud profiles, or windows.
- Keep network, disk, exports, and collector teardown off the UI thread. Stop each worker independently and observe background failures.
- Calculate rates using actual elapsed time; use monotonic clocks for sampling. Preserve measured zero values and missing intervals separately. Never reconstruct seconds from minute averages.
- Cloud history is memory-only, at most 300 second slots. Maintain one SSH connection and one read-only remote sampler, with bounded timeout and reconnect delay.
- Store passwords in Windows Credential Manager. Do not put real endpoints, credentials, private FRP configuration, histories, or diagnostic exports in source or releases. Validate the SSH host key.
- Split files by responsibility as features grow. Avoid broad rewrites, duplicate protocol logic, global mutable state, and unrelated changes.
- Validate changed behavior with meaningful tests, OpenXML validation for workbook changes, and previews for layout changes. Build and run the existing self-tests before release.

These rules apply to all future changes in this directory.
