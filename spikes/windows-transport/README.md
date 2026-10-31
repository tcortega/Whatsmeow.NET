# Spike: Windows transport

Throwaway spike for ARCHITECTURE §14 question 2: AF_UNIX streaming stability on Windows (grpc-go #7039) vs named pipes.
Go 1.27 grpc-go server (`go/server`), .NET Native AOT client (`dotnet`), and a Go-only control in the #7039 reproducer shape (`go/control`).
Run by `.github/workflows/spike-windows-transport.yml`. Not product code; delete once the ADR is written.
