.DEFAULT_GOAL := help

help:
	@echo "LivingDocs — available targets"
	@echo ""
	@echo "  make build            Build all projects"
	@echo "  make test             Run all tests"
	@echo "  make scan REPO=<path> Detect stale docs in a local repo"
	@echo "  make publish          Publish MCP server binary to out/"
	@echo ""

build:
	dotnet build LivingDocs.slnx

test:
	dotnet test LivingDocs.slnx

scan:
	@test -n "$(REPO)" || (echo "Usage: make scan REPO=/path/to/repo" && exit 1)
	dotnet run --project src/LivingDocs.McpServer -- scan $(REPO)

publish:
	dotnet publish src/LivingDocs.McpServer -c Release -o out/
