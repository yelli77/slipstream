#!/bin/bash
# How was the 331 roundtrip test run live? Check Client.cs trigger + server container layout
ssh root@31.97.125.237 "cd /docker/starttruckmp/src && sed -n 105,165p Client/Client.cs"
