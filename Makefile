build:
	dotnet build LivingDocs.sln

test:
	dotnet test LivingDocs.sln

scan:
	dotnet run --project src/LivingDocs.McpServer -- scan $(REPO)
