# Deploying SentryApp to IIS

Do not run `dotnet publish --output` directly against the directory used by an
IIS application. The IIS worker process loads `SentryApp.dll` from that
directory and Windows will prevent `dotnet publish` from replacing the loaded
file.

Use the deployment script from the repository root instead:

```powershell
.\scripts\Publish-Iis.ps1
```

The default destination is `artifacts\SentryApp-IIS`. To deploy to another IIS
physical path, pass it explicitly:

```powershell
.\scripts\Publish-Iis.ps1 -Destination 'C:\inetpub\wwwroot\SentryApp'
```

The script first publishes to a unique staging directory. Only after a
successful build does it create `app_offline.htm` in the IIS directory, wait
for ASP.NET Core Module to release the application assemblies, and copy the
new files. It removes both the offline marker and staging directory in a
`finally` block, including when copying fails.

If the application pool takes longer than ten seconds to shut down, increase
the wait without changing the script:

```powershell
.\scripts\Publish-Iis.ps1 -ShutdownWaitSeconds 20
```
