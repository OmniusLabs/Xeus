#!/bin/bash
cd $(dirname $0)

dotnet run --project ../src/Omnius.Xeus.Daemon.Implementations/ -- "./daemon_2/state" "./daemon_2/logs"
