using Windows.System;
using WinARD.Application.Ports;
using WinARD.Desktop.Input;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class WindowsInputMapperTests
{
    [Fact]
    public async Task Control_down_then_release_all_sends_left_control_up()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = new WindowsInputMapper((keysym, down, _) =>
        {
            events.Add((keysym, down));
            return ValueTask.CompletedTask;
        });

        await mapper.KeyDownAsync(VirtualKey.Control, 0x1d, isExtended: false, null, CancellationToken.None);
        await mapper.ReleaseAllAsync(CancellationToken.None);

        Assert.Equal([(0xffe3u, true), (0xffe3u, false)], events);
    }

    [Fact]
    public async Task Modifier_sides_and_common_keys_map_to_x11_keysyms()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.KeyDownAsync(VirtualKey.Control, 0x1d, true, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.Shift, 0x36, false, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.F5, 0x3f, false, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.Delete, 0x53, true, null, CancellationToken.None);

        Assert.Equal([0xffe4u, 0xffe2u, 0xffc2u, 0xffffu], events.Select(item => item.Keysym));
    }

    [Fact]
    public async Task Repeated_printable_key_down_is_forwarded_but_one_up_clears_pressed_state()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);
        await mapper.KeyUpAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);
        await mapper.KeyUpAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);
        await mapper.ReleaseAllAsync(CancellationToken.None);

        Assert.Equal(
            [('a', true), ('a', true), ('a', true), ('a', false)],
            events.Select(item => ((char)item.Keysym, item.Down)));
    }

    [Fact]
    public async Task Repeated_navigation_key_down_is_forwarded_and_release_all_releases_once()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.KeyDownAsync(VirtualKey.Left, 0x4b, true, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.Left, 0x4b, true, null, CancellationToken.None);
        await mapper.ReleaseAllAsync(CancellationToken.None);

        Assert.Equal([(0xff51u, true), (0xff51u, true), (0xff51u, false)], events);
    }

    [Fact]
    public async Task Repeated_modifier_and_lock_key_down_is_suppressed()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.KeyDownAsync(VirtualKey.Shift, 0x2a, false, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.Shift, 0x2a, false, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.CapitalLock, 0x3a, false, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.CapitalLock, 0x3a, false, null, CancellationToken.None);
        await mapper.ReleaseAllAsync(CancellationToken.None);

        Assert.Equal(
            [(0xffe1u, true), (0xffe5u, true), (0xffe5u, false), (0xffe1u, false)],
            events);
    }

    [Fact]
    public async Task Unknown_up_is_ignored_and_release_all_releases_only_remaining_pressed_keys()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.B, 0x30, false, "b", CancellationToken.None);
        await mapper.KeyUpAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);
        await mapper.KeyUpAsync(VirtualKey.C, 0x2e, false, "c", CancellationToken.None);
        await mapper.ReleaseAllAsync(CancellationToken.None);

        Assert.Equal(
            [('a', true), ('b', true), ('a', false), ('b', false)],
            events.Select(item => ((char)item.Keysym, item.Down)));
    }

    [Fact]
    public async Task Failed_key_up_remains_pressed_so_release_all_retries_it()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var failedFirstKeyUp = false;
        var mapper = new WindowsInputMapper((keysym, down, _) =>
        {
            events.Add((keysym, down));
            if (!down && !failedFirstKeyUp)
            {
                failedFirstKeyUp = true;
                return ValueTask.FromException(new InvalidOperationException("send failed"));
            }

            return ValueTask.CompletedTask;
        });
        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mapper.KeyUpAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None).AsTask());
        await mapper.ReleaseAllAsync(CancellationToken.None);

        Assert.Equal([('a', true), ('a', false), ('a', false)],
            events.Select(item => ((char)item.Keysym, item.Down)));
    }

    [Fact]
    public async Task Letter_without_text_maps_to_lowercase_keysym_for_remote_shift_handling()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, null, CancellationToken.None);

        Assert.Equal((0x61u, true), Assert.Single(events));
    }

    [Fact]
    public async Task Text_input_sends_unicode_keysym_down_and_up()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.TextInputAsync("你", CancellationToken.None);

        Assert.Equal([(0x01004f60u, true), (0x01004f60u, false)], events);
    }

    [Theory]
    [InlineData("é", 0x000000e9u)]
    [InlineData("你", 0x01004f60u)]
    [InlineData("😀", 0x0101f600u)]
    public void Unicode_text_uses_x11_unicode_keysym_strategy(string text, uint expected)
    {
        Assert.Equal(expected, WindowsInputMapper.ToUnicodeKeysym(text));
    }

    [Fact]
    public void Pointer_buttons_and_wheel_use_rfb_masks()
    {
        Assert.Equal(
            0b0000_0111,
            WindowsInputMapper.ToPointerMask(
                RemotePointerButtons.Left | RemotePointerButtons.Middle | RemotePointerButtons.Right));
        Assert.Equal((byte)0b0000_1001, WindowsInputMapper.WithWheel(0b0000_0001, 120));
        Assert.Equal((byte)0b0001_0001, WindowsInputMapper.WithWheel(0b0000_0001, -120));
    }

    [Theory]
    [InlineData(RemotePointerButtons.Left, (byte)1)]
    [InlineData(RemotePointerButtons.Right, (byte)2)]
    [InlineData(RemotePointerButtons.Middle, (byte)4)]
    public void Pointer_buttons_use_apple_ard_bit_order(RemotePointerButtons buttons, byte expected)
    {
        Assert.Equal(expected, WindowsInputMapper.ToPointerMask(buttons));
    }

    [Fact]
    public async Task Secure_attention_sequence_is_sent_remotely_in_order()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);

        await mapper.SendSecureAttentionSequenceAsync(CancellationToken.None);

        Assert.Equal(
            [
                (0xffe3u, true), (0xffe9u, true), (0xffffu, true),
                (0xffffu, false), (0xffe9u, false), (0xffe3u, false),
            ],
            events);
    }

    [Fact]
    public async Task Secure_attention_sequence_is_serialized_with_normal_input()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var firstCadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalInputEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockFirstCad = true;
        var mapper = new WindowsInputMapper(async (keysym, down, _) =>
        {
            lock (events)
            {
                events.Add((keysym, down));
            }

            if (keysym == 0xffe3u && down && blockFirstCad)
            {
                blockFirstCad = false;
                firstCadEntered.TrySetResult();
                await releaseFirstCad.Task;
            }
            else if (keysym == 'a' && down)
            {
                normalInputEntered.TrySetResult();
            }
        });

        var cad = mapper.SendSecureAttentionSequenceAsync(CancellationToken.None).AsTask();
        await firstCadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var normalInput = mapper.KeyDownAsync(
            VirtualKey.A, 0x1e, false, "a", CancellationToken.None).AsTask();
        var normalInputWasBlocked = !normalInputEntered.Task.IsCompleted;
        releaseFirstCad.TrySetResult();
        await Task.WhenAll(cad, normalInput).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(normalInputWasBlocked);
        Assert.Equal(
            [
                (0xffe3u, true), (0xffe9u, true), (0xffffu, true),
                (0xffffu, false), (0xffe9u, false), (0xffe3u, false),
                (0x61u, true),
            ],
            events);
    }

    [Fact]
    public async Task Secure_attention_sequence_does_not_release_user_held_modifiers()
    {
        var events = new List<(uint Keysym, bool Down)>();
        var mapper = Mapper(events);
        await mapper.KeyDownAsync(VirtualKey.Control, 0x1d, false, null, CancellationToken.None);
        await mapper.KeyDownAsync(VirtualKey.Menu, 0x38, true, null, CancellationToken.None);

        await mapper.SendSecureAttentionSequenceAsync(CancellationToken.None);
        await mapper.ReleaseAllAsync(CancellationToken.None);

        Assert.Equal(
            [
                (0xffe3u, true), (0xffeau, true),
                (0xffffu, true), (0xffffu, false),
                (0xffeau, false), (0xffe3u, false),
            ],
            events);
    }

    [Fact]
    public async Task Secure_attention_sequence_release_is_bounded_when_sender_never_completes()
    {
        var releaseAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapper = new WindowsInputMapper((_, down, _) =>
        {
            if (down)
            {
                return ValueTask.CompletedTask;
            }

            releaseAttempted.TrySetResult();
            return new ValueTask(neverCompletes.Task);
        });

        await mapper.SendSecureAttentionSequenceAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(releaseAttempted.Task.IsCompleted);
    }

    [Fact]
    public async Task Dispose_is_bounded_when_release_sender_never_completes()
    {
        var releaseAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapper = new WindowsInputMapper((_, down, _) =>
        {
            if (down)
            {
                return ValueTask.CompletedTask;
            }

            releaseAttempted.TrySetResult();
            return new ValueTask(neverCompletes.Task);
        });
        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);

        await mapper.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(releaseAttempted.Task.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            mapper.KeyDownAsync(VirtualKey.B, 0x30, false, "b", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Dispose_completes_and_clears_state_when_release_sender_fails()
    {
        var mapper = new WindowsInputMapper((_, down, _) => down
            ? ValueTask.CompletedTask
            : ValueTask.FromException(new InvalidOperationException("release failed")));
        await mapper.KeyDownAsync(VirtualKey.A, 0x1e, false, "a", CancellationToken.None);

        await mapper.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            mapper.KeyDownAsync(VirtualKey.B, 0x30, false, "b", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Dispose_is_bounded_when_an_existing_input_holds_the_gate_forever()
    {
        var senderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapper = new WindowsInputMapper((_, _, _) =>
        {
            senderEntered.TrySetResult();
            return new ValueTask(neverCompletes.Task);
        });
        var blockedInput = mapper.KeyDownAsync(
            VirtualKey.A, 0x1e, false, "a", CancellationToken.None).AsTask();
        await senderEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(blockedInput.IsCompleted);

        await mapper.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            mapper.KeyDownAsync(VirtualKey.B, 0x30, false, "b", CancellationToken.None).AsTask());
    }

    private static WindowsInputMapper Mapper(List<(uint Keysym, bool Down)> events) =>
        new((keysym, down, _) =>
        {
            events.Add((keysym, down));
            return ValueTask.CompletedTask;
        });
}
