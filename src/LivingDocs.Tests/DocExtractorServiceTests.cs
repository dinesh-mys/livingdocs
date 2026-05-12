using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class DocExtractorServiceTests
{
    private readonly DocExtractorService _sut = new();

    // ── C# ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_CSharp_XmlDocComment_ReturnsChunk()
    {
        var content = """
            /// <summary>Calculates the total price.</summary>
            public decimal Calculate(int qty) => qty * 10;
            """;

        var result = (await _sut.ExtractAsync("Service.cs", content)).ToList();

        Assert.Single(result);
        Assert.Contains("Calculates the total price", result[0].Content);
        Assert.Equal("Calculate", result[0].ParentSymbol);
        Assert.Equal("csharp", result[0].Language);
        Assert.Equal(1, result[0].LineNumber);
    }

    [Fact]
    public async Task ExtractAsync_CSharp_MultilineXmlDoc_ConcatenatesContent()
    {
        var content = """
            /// <summary>
            /// Sends a welcome email
            /// to the new user.
            /// </summary>
            public void SendEmail(string to) {}
            """;

        var result = (await _sut.ExtractAsync("Mailer.cs", content)).ToList();

        Assert.Single(result);
        Assert.Contains("Sends a welcome email", result[0].Content);
        Assert.Contains("to the new user", result[0].Content);
    }

    [Fact]
    public async Task ExtractAsync_CSharp_NoDocComments_ReturnsEmpty()
    {
        var content = """
            // regular comment
            public void DoWork() {}
            """;

        var result = await _sut.ExtractAsync("Worker.cs", content);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractAsync_CSharp_MultipleDocBlocks_ReturnsAll()
    {
        var content = """
            /// <summary>Opens a connection.</summary>
            public void Open() {}

            /// <summary>Closes the connection.</summary>
            public void Close() {}
            """;

        var result = (await _sut.ExtractAsync("Db.cs", content)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Open", result[0].ParentSymbol);
        Assert.Equal("Close", result[1].ParentSymbol);
    }

    // ── TypeScript ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_TypeScript_JSDoc_ReturnsChunk()
    {
        var content = """
            /**
             * Fetches user by ID from the database.
             * @param id The user identifier
             */
            async function getUser(id: string) {}
            """;

        var result = (await _sut.ExtractAsync("api.ts", content)).ToList();

        Assert.Single(result);
        Assert.Contains("Fetches user by ID", result[0].Content);
        Assert.Equal("getUser", result[0].ParentSymbol);
        Assert.Equal("typescript", result[0].Language);
    }

    [Fact]
    public async Task ExtractAsync_TypeScript_SingleLineJSDoc_ReturnsChunk()
    {
        var content = "/** Returns current timestamp. */\nconst now = () => Date.now();";

        var result = (await _sut.ExtractAsync("utils.ts", content)).ToList();

        Assert.Single(result);
        Assert.Contains("Returns current timestamp", result[0].Content);
    }

    [Fact]
    public async Task ExtractAsync_JavaScript_JSDoc_DetectsCorrectLanguage()
    {
        var content = """
            /** Formats a currency value. */
            function formatCurrency(value) {}
            """;

        var result = (await _sut.ExtractAsync("format.js", content)).ToList();

        Assert.Single(result);
        Assert.Equal("javascript", result[0].Language);
        Assert.Equal("formatCurrency", result[0].ParentSymbol);
    }

    // ── Python ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_Python_FunctionDocstring_ReturnsChunk()
    {
        var content = "def calculate_tax(amount):\n    \"\"\"Calculate tax for the given amount.\"\"\"\n    return amount * 0.2";

        var result = (await _sut.ExtractAsync("tax.py", content)).ToList();

        Assert.Single(result);
        Assert.Contains("Calculate tax", result[0].Content);
        Assert.Equal("calculate_tax", result[0].ParentSymbol);
        Assert.Equal("python", result[0].Language);
    }

    [Fact]
    public async Task ExtractAsync_Python_MultilineDocstring_ConcatenatesContent()
    {
        var content = "def process():\n    \"\"\"\n    Processes the incoming request\n    and returns a response.\n    \"\"\"\n    pass";

        var result = (await _sut.ExtractAsync("handler.py", content)).ToList();

        Assert.Single(result);
        Assert.Contains("Processes the incoming request", result[0].Content);
        Assert.Contains("and returns a response", result[0].Content);
    }

    // ── Unsupported ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_UnsupportedExtension_ReturnsEmpty()
    {
        var result = await _sut.ExtractAsync("data.json", "{ \"key\": \"value\" }");

        Assert.Empty(result);
    }
}
