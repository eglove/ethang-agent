using eThangAgent.Lsp.ACL;

#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Lsp.ACL.Tests;

public class LspRenameEditParsingTests
{
  [Fact]
  public void Parse_ChangesShape_ReadsEveryEdit()
  {
    const string json = "{\"changes\":{\"file:///ws/A.cs\":[{\"range\":{\"start\":{\"line\":3,\"character\":10},\"end\":{\"line\":3,\"character\":13}},\"newText\":\"Bar\"}]}}";
    LspRenameEditSet set = LspRenameEdits.Parse(json);
    Assert.False(set.HasFileRenames);
    LspTextDocumentEdit doc = Assert.Single(set.Documents);
    Assert.Equal("file:///ws/A.cs", doc.Uri);
    LspTextEdit edit = Assert.Single(doc.Edits);
    Assert.Equal(3, edit.StartLine);
    Assert.Equal(10, edit.StartCharacter);
    Assert.Equal(13, edit.EndCharacter);
    Assert.Equal("Bar", edit.NewText);
  }

  [Fact]
  public void Parse_DocumentChangesShape_ReadsEveryEdit()
  {
    const string json = "{\"documentChanges\":[{\"textDocument\":{\"uri\":\"file:///ws/A.cs\",\"version\":1},\"edits\":[{\"range\":{\"start\":{\"line\":1,\"character\":0},\"end\":{\"line\":1,\"character\":2}},\"newText\":\"Baz\"}]}]}";
    LspRenameEditSet set = LspRenameEdits.Parse(json);
    Assert.False(set.HasFileRenames);
    Assert.Equal("file:///ws/A.cs", Assert.Single(set.Documents).Uri);
  }

  [Fact]
  public void Parse_FileRenameOperation_SetsHasFileRenames()
  {
    const string json = "{\"documentChanges\":[{\"kind\":\"rename\",\"oldUri\":\"file:///ws/A.cs\",\"newUri\":\"file:///ws/B.cs\"}]}";
    Assert.True(LspRenameEdits.Parse(json).HasFileRenames);
  }

  [Fact]
  public void Parse_EmptyEditSet_IsEmptyNotAnError()
  {
    LspRenameEditSet set = LspRenameEdits.Parse("{}");
    Assert.Empty(set.Documents);
    Assert.False(set.HasFileRenames);
  }
}
