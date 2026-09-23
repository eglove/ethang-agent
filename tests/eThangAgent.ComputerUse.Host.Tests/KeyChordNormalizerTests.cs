
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>KeyChordNormalizer table tests (task 17): short names, X-keysym style,
///     modifiers, aliases (Return/Enter, Escape/Esc, PageUp/prior, super->win),
///     F1-F24, and typed invalid_request errors that name the unknown key.</summary>
public class KeyChordNormalizerTests
{
  [Fact]
  public void Letter_ProducesVirtualKeyCode()
  {
    KeyChord chord = KeyChordNormalizer.Parse("a");
    Assert.Equal((ushort)0x41, chord.KeyCode);
    Assert.Equal(KeyModifiers.None, chord.Modifiers);
  }

  [Fact]
  public void UppercaseLetter_IsNormalizedToVirtualKey()
  {
    KeyChord chord = KeyChordNormalizer.Parse("Z");
    Assert.Equal((ushort)0x5A, chord.KeyCode);
  }

  [Fact]
  public void Digit_ProducesVirtualKeyCode()
  {
    KeyChord chord = KeyChordNormalizer.Parse("5");
    Assert.Equal((ushort)0x35, chord.KeyCode);
  }

  [Theory]
  [InlineData("Return", 0x0D)]
  [InlineData("Enter", 0x0D)]
  [InlineData("Tab", 0x09)]
  [InlineData("Escape", 0x1B)]
  [InlineData("Esc", 0x1B)]
  [InlineData("Backspace", 0x08)]
  [InlineData("Delete", 0x2E)]
  [InlineData("Del", 0x2E)]
  [InlineData("Insert", 0x2D)]
  [InlineData("Home", 0x24)]
  [InlineData("End", 0x23)]
  [InlineData("PageUp", 0x21)]
  [InlineData("prior", 0x21)]
  [InlineData("PageDown", 0x22)]
  [InlineData("next", 0x22)]
  [InlineData("Up", 0x26)]
  [InlineData("Down", 0x28)]
  [InlineData("Left", 0x25)]
  [InlineData("Right", 0x27)]
  [InlineData("Space", 0x20)]
  [InlineData("F1", 0x70)]
  [InlineData("F12", 0x7B)]
  [InlineData("F24", 0x87)]
  public void ShortNames_MapToVirtualKeys(string name, int expectedKeyCode)
  {
    KeyChord chord = KeyChordNormalizer.Parse(name);
    Assert.Equal((ushort)expectedKeyCode, chord.KeyCode);
  }


  [Fact]
  public void PlusJoinedModifiers_CollectIntoChord()
  {
    KeyChord chord = KeyChordNormalizer.Parse("ctrl+alt+Delete");
    Assert.Equal((ushort)0x2E, chord.KeyCode);
    Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, chord.Modifiers);
  }

  [Fact]
  public void XKeysymStyle_ModifierSuffix_IsStripped()
  {
    // Control_L+a: the keysym suffix _L/_R is not a modifier declaration, it is the
    // key itself; the leading token names the modifier per the chord grammar.
    KeyChord chord = KeyChordNormalizer.Parse("Control_L+a");
    Assert.Equal((ushort)0x41, chord.KeyCode);
    Assert.Equal(KeyModifiers.Control, chord.Modifiers);
  }

  [Fact]
  public void ShiftL_A_Works()
  {
    KeyChord chord = KeyChordNormalizer.Parse("Shift_L+a");
    Assert.Equal((ushort)0x41, chord.KeyCode);
    Assert.Equal(KeyModifiers.Shift, chord.Modifiers);
  }

  [Fact]
  public void Super_AliasesToWin()
  {
    KeyChord chord = KeyChordNormalizer.Parse("super+d");
    Assert.Equal((ushort)0x44, chord.KeyCode);
    Assert.Equal(KeyModifiers.Win, chord.Modifiers);
  }

  [Fact]
  public void CtrlSideSuffix_MarksTheModifier_NotAKey()
  {
    // Ctrl_L+a: _L is the keysym side of the modifier, not a separate key.
    KeyChord chord = KeyChordNormalizer.Parse("Ctrl_L+a");
    Assert.Equal((ushort)0x41, chord.KeyCode);
    Assert.Equal(KeyModifiers.Control, chord.Modifiers);
  }

  [Fact]
  public void UnderscoreGlue_BetweenModifiers_IsRejectedTyped()
  {
    KeyChordException error = Assert.Throws<KeyChordException>(() => KeyChordNormalizer.Parse("ctrl+alt_shift+Del"));
    Assert.Contains("alt_shift", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void UnknownKey_IsRejectedWithTheKeyName()
  {
    KeyChordException error = Assert.Throws<KeyChordException>(() => KeyChordNormalizer.Parse("ctrl+ novas "));
    Assert.Contains("novas", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void EmptyChord_IsRejected() => _ = Assert.Throws<KeyChordException>(() => KeyChordNormalizer.Parse(string.Empty));
}
