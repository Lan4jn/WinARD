using Windows.System;

namespace WinARD.Desktop.Input;

internal sealed class RemoteTextInputBuffer
{
    private KeyIdentity? _pendingPhysicalCharacter;
    private char? _pendingHighSurrogate;

    public void OnPhysicalKeyDown(VirtualKey key, int scanCode, bool isExtended)
    {
        if (UsesPhysicalTextPath(key))
        {
            _pendingPhysicalCharacter = new KeyIdentity(key, scanCode, isExtended);
        }
    }

    public void OnPhysicalKeyUp(VirtualKey key, int scanCode, bool isExtended)
    {
        if (_pendingPhysicalCharacter == new KeyIdentity(key, scanCode, isExtended))
        {
            _pendingPhysicalCharacter = null;
        }
    }

    public string? AcceptCharacter(char character)
    {
        if (_pendingPhysicalCharacter is not null)
        {
            _pendingPhysicalCharacter = null;
            return null;
        }

        if (char.IsHighSurrogate(character))
        {
            _pendingHighSurrogate = character;
            return null;
        }

        if (char.IsLowSurrogate(character))
        {
            if (_pendingHighSurrogate is not { } high)
            {
                return null;
            }

            _pendingHighSurrogate = null;
            return new string([high, character]);
        }

        _pendingHighSurrogate = null;
        return character.ToString();
    }

    private static bool UsesPhysicalTextPath(VirtualKey key)
    {
        var value = (int)key;
        return value is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a or 0x20;
    }

    private readonly record struct KeyIdentity(VirtualKey Key, int ScanCode, bool IsExtended);
}
