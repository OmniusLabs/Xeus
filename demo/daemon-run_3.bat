cd /d %~dp0

dotnet run --project ../src/Omnius.Xeus.Daemon.Implementations/ -- "./daemon_3/state" "./daemon_3/logs"
