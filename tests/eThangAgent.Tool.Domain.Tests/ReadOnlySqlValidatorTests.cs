using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class ReadOnlySqlValidatorTests
{
  private static void Accepts(string sql) =>
      Assert.Null(ReadOnlySqlValidator.Validate(sql));

  private static void Rejects(string sql)
  {
    DomainError? error = ReadOnlySqlValidator.Validate(sql);
    Assert.NotNull(error);
    Assert.Equal(ToolErrorCodes.InvalidSql, error.Code);
  }

  // ---- Accepted: single read-only queries ----

  [Theory]
  [InlineData("SELECT 1")]
  [InlineData("  select name from agents;")]
  [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
  [InlineData("/* lead-in comment */ SELECT 1")]
  [InlineData("-- lead-in comment\nSELECT 1")]
  [InlineData("SELECT 1 /* trailing */;")]
  public void SingleReadOnlyQueries_AreAccepted(string sql) => Accepts(sql);

  [Fact]
  public void SemicolonInsideStringLiteral_IsNotAStatementSeparator() =>
      Accepts("SELECT * FROM t WHERE a = 'x;y'");

  [Fact]
  public void DoubledQuoteInsideStringLiteral_IsAnEscape() =>
      Accepts("SELECT 'it''s; fine'");

  [Fact]
  public void SemicolonInsideBracketedIdentifier_IsNotAStatementSeparator() =>
      Accepts("SELECT * FROM [weird;table]");

  [Fact]
  public void AttachInsideStringLiteral_IsNotTheAttachStatement() =>
      Accepts("SELECT 'attach' AS word");

  [Fact]
  public void AttachInsideComment_IsNotTheAttachStatement() =>
      Accepts("SELECT 1 /* attach anything */");

  // ---- Accepted by design: the lexical gate is not the enforcement ----

  /// <summary>The validator deliberately does not parse statement bodies: WITH…INSERT
  ///     passes here and is rejected by the read-only connection (covered in
  ///     Storage.ACL tests).</summary>
  [Fact]
  public void WritableCteForm_PassesTheLexicalGate_ConnectionIsTheBackstop() =>
      Accepts("WITH d AS (SELECT 1) INSERT INTO t SELECT * FROM d");

  [Fact]
  public void BareSelectKeyword_PassesTheLexicalGate_SyntaxIsTheEngines() =>
      Accepts("SELECT");

  // ---- Rejected: not a single read-only query ----

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("-- only a comment")]
  [InlineData("/* only a comment */")]
  [InlineData("INSERT INTO t VALUES (1)")]
  [InlineData("UPDATE t SET a = 1")]
  [InlineData("DELETE FROM t")]
  [InlineData("PRAGMA user_version")]
  [InlineData("CREATE TABLE x (a INT)")]
  [InlineData("DROP TABLE agents")]
  [InlineData("VACUUM")]
  [InlineData("VALUES (1)")]
  [InlineData("ATTACH DATABASE 'x.db' AS x")]
  [InlineData("DETACH x")]
  [InlineData("SELECT 1; SELECT 2")]
  [InlineData("SELECT 1;DROP TABLE agents")]
  [InlineData("WITH d AS (SELECT 1) SELECT 1; DELETE FROM t")]
  public void NonSingleReadOnlyStatements_AreRejected(string sql) => Rejects(sql);

  [Fact]
  public void AttachMidStatement_IsRejected() =>
      Rejects("SELECT * FROM agents ATTACH x");

  [Fact]
  public void UnterminatedStringLiteral_IsRejected() =>
      Rejects("SELECT 'unterminated");

  [Fact]
  public void UnterminatedBracketedIdentifier_IsRejected() =>
      Rejects("SELECT [unterminated");
}
