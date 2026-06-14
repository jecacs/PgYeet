using BenchmarkDotNet.Running;
using Bench;

// Run in Release against the Docker Postgres:  dotnet run -c Release --project Bench
BenchmarkRunner.Run<InsertBenchmark>();
