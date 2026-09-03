using Dsf.Core.Charters;
using Xunit;

namespace Dsf.Core.Tests;

public sealed class CharterMarkdownTests
{
    private const string Valid = """
        <!-- dsf:charter schema_version=1 -->
        # Product Charter: demo

        ## Vision
        Ship demo value.

        ## Target Users
        Operators.

        ## Goals
        - Deliver value

        ## Non-Goals
        - Boil the ocean

        ## Success Metrics
        - Weekly active operators

        ## Constraints
        Azure only.

        ## Glossary
        - Charter: human-owned intent
        """;

    [Fact]
    public void Parse_reads_every_required_section()
    {
        var charter = CharterMarkdown.Parse(Valid, product: "demo");

        Assert.Equal("demo", charter.Product);
        Assert.Equal(1, charter.SchemaVersion);
        Assert.Equal("Ship demo value.", charter.Vision);
        Assert.Equal("Operators.", charter.TargetUsers);
        Assert.Equal(["Deliver value"], charter.Goals);
        Assert.Equal(["Boil the ocean"], charter.NonGoals);
        Assert.Equal(["Weekly active operators"], charter.SuccessMetrics);
        Assert.Equal("Azure only.", charter.Constraints);
        Assert.Equal("human-owned intent", charter.Glossary["Charter"]);
    }

    [Fact]
    public void Parse_collects_every_diagnostic_before_failing()
    {
        var error = Assert.Throws<CharterParseException>(
            () => CharterMarkdown.Parse("# Product Charter: demo\n\n## Vision\nA\n", product: "demo"));

        Assert.Contains(error.Diagnostics, d => d.Contains("marker", StringComparison.Ordinal));
        Assert.Contains(error.Diagnostics, d => d.Contains("## Goals", StringComparison.Ordinal));
        Assert.Contains(error.Diagnostics, d => d.Contains("## Constraints", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_unsupported_schema_versions()
    {
        var error = Assert.Throws<CharterParseException>(
            () => CharterMarkdown.Parse(Valid.Replace("schema_version=1", "schema_version=2"), product: "demo"));

        Assert.Contains(error.Diagnostics, d => d.Contains("schema_version 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_merge_conflict_markers()
    {
        var error = Assert.Throws<CharterParseException>(
            () => CharterMarkdown.Parse(Valid + "\n<<<<<<< HEAD\n", product: "demo"));

        Assert.Contains(error.Diagnostics, d => d.Contains("merge conflict", StringComparison.Ordinal));
    }

    [Fact]
    public void GitBlobSha_matches_git_hash_object()
    {
        // `printf 'hello\n' | git hash-object --stdin`
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", CharterMarkdown.GitBlobSha("hello\n"));
    }
}
