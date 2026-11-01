using Microsoft.Win32;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass.Tests;

public class SystemProxyManagerTests
{
    private const string Pac = "http://127.0.0.1:18081/proxy.pac";

    private static (SystemProxyManager Manager, MemoryProxyStore Store, MemoryStateStore State) Make(string? current = null)
    {
        var store = new MemoryProxyStore { Value = current };
        var state = new MemoryStateStore();
        return (new SystemProxyManager(store, state, Pac), store, state);
    }

    [Fact]
    public void Apply_saves_the_previous_value_first_then_writes_ours_then_notifies()
    {
        var (m, store, state) = Make("http://corp.example/wpad.pac");
        m.Apply();
        Assert.Equal(Pac, store.Value);
        Assert.Equal(new ProxyState(Pac, "http://corp.example/wpad.pac"), state.State);
        Assert.Equal(1, store.Notifications);
    }

    [Fact]
    public void Apply_with_no_previous_value_saves_none()
    {
        var (m, _, state) = Make(null);
        m.Apply();
        Assert.Equal(new ProxyState(Pac, null), state.State);
    }

    [Fact]
    public void Restore_puts_the_previous_value_back_and_forgets_the_state()
    {
        var (m, store, state) = Make("http://corp.example/wpad.pac");
        m.Apply();
        var result = m.Restore();
        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.Equal("http://corp.example/wpad.pac", store.Value);
        Assert.Null(state.State);
        Assert.Equal(2, store.Notifications);
    }

    [Fact]
    public void Restore_with_no_previous_value_removes_the_value()
    {
        var (m, store, state) = Make(null);
        m.Apply();
        Assert.Equal(RestoreOutcome.Restored, m.Restore().Outcome);
        Assert.Null(store.Value);
        Assert.Equal("delete", store.Journal.Last());
        Assert.Null(state.State);
    }

    [Fact]
    public void Restoring_twice_is_harmless()
    {
        var (m, store, _) = Make("http://old.example/p.pac");
        m.Apply();
        m.Restore();
        int writes = store.Writes;
        var second = m.Restore();
        Assert.Equal(RestoreOutcome.NothingToRestore, second.Outcome);
        Assert.Equal(writes, store.Writes);
        Assert.Equal("http://old.example/p.pac", store.Value);
    }

    [Fact]
    public void A_value_somebody_else_set_in_the_meantime_is_never_overwritten()
    {
        var (m, store, state) = Make("http://old.example/p.pac");
        m.Apply();
        store.Value = "http://another-tool.example/proxy.pac";   // changed behind our back
        int writes = store.Writes;
        var result = m.Restore();
        Assert.Equal(RestoreOutcome.NothingToRestore, result.Outcome);
        Assert.Equal("http://another-tool.example/proxy.pac", store.Value);
        Assert.Equal(writes, store.Writes);
        Assert.Null(state.State);   // the saved value is stale now: dropped
    }

    [Fact]
    public void A_script_on_another_local_port_is_not_ours()
    {
        var (m, store, _) = Make("http://127.0.0.1:9999/proxy.pac");
        Assert.False(m.IsApplied());
        Assert.Equal(RestoreOutcome.NothingToRestore, m.Restore().Outcome);
        Assert.Equal("http://127.0.0.1:9999/proxy.pac", store.Value);
        Assert.True(m.IsOurs("http://127.0.0.1:18081/anything"));
        Assert.False(m.IsOurs("http://127.0.0.1:180810/proxy.pac"));
        Assert.False(m.IsOurs(null));
    }

    // ---- crash recovery -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Crash_recovery_a_new_run_restores_what_the_dead_run_saved()
    {
        var store = new MemoryProxyStore { Value = "http://old.example/p.pac" };
        var state = new MemoryStateStore();
        new SystemProxyManager(store, state, Pac).Apply();   // the run that crashed: never restored
        Assert.Equal(Pac, store.Value);

        var next = new SystemProxyManager(store, state, Pac);   // the next run starts from the same two stores
        var result = next.Restore();
        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.Equal("http://old.example/p.pac", store.Value);
        Assert.Null(state.State);
    }

    [Fact]
    public void Applying_over_our_own_stale_address_keeps_the_original_saved_value()
    {
        var store = new MemoryProxyStore { Value = "http://old.example/p.pac" };
        var state = new MemoryStateStore();
        new SystemProxyManager(store, state, Pac).Apply();   // crashed run
        var next = new SystemProxyManager(store, state, Pac);
        next.Apply();   // the new run turns on without a cleanup in between
        Assert.Equal("http://old.example/p.pac", state.State!.Previous);   // not "our own address"
        next.Restore();
        Assert.Equal("http://old.example/p.pac", store.Value);
    }

    [Fact]
    public void An_address_that_looks_like_ours_but_this_module_never_set_is_left_alone()
    {
        // The standalone Steam Bypass app writes the very same address. With no saved state of this module, it is not ours to undo.
        var (m, store, _) = Make(Pac);
        Assert.Equal(RestoreOutcome.NothingToRestore, m.Restore().Outcome);
        Assert.Equal(Pac, store.Value);
        Assert.Equal(0, store.Writes);
        Assert.True(m.IsApplied());   // it does look like ours, which is what the status verb reports
    }

    [Fact]
    public void A_registry_write_that_fails_changes_nothing_and_leaves_no_state_behind()
    {
        var (m, store, state) = Make("http://old.example/p.pac");
        store.WriteThrows = new UnauthorizedAccessException("denied");
        Assert.Throws<UnauthorizedAccessException>(m.Apply);
        Assert.Equal("http://old.example/p.pac", store.Value);
        Assert.Null(state.State);
    }

    [Fact]
    public void A_restore_that_fails_keeps_the_state_so_the_next_try_can_succeed()
    {
        var (m, store, state) = Make("http://old.example/p.pac");
        m.Apply();
        store.WriteThrows = new IOException("registry busy");
        var failed = m.Restore();
        Assert.Equal(RestoreOutcome.Failed, failed.Outcome);
        Assert.Equal("registry busy", failed.Error);
        Assert.NotNull(state.State);
        Assert.Equal(Pac, store.Value);

        store.WriteThrows = null;
        Assert.Equal(RestoreOutcome.Restored, m.Restore().Outcome);
        Assert.Equal("http://old.example/p.pac", store.Value);
    }

    [Fact]
    public void An_unreadable_registry_is_a_failed_restore_not_an_exception()
    {
        var (m, store, _) = Make(Pac);
        store.ReadThrows = new IOException("no access");
        Assert.Equal(RestoreOutcome.Failed, m.Restore().Outcome);
        Assert.False(m.IsApplied());
    }

    [Fact]
    public void A_notification_that_fails_does_not_undo_the_change()
    {
        var (m, store, _) = Make(null);
        var throwing = new ThrowingNotify { Value = null };
        var manager = new SystemProxyManager(throwing, new MemoryStateStore(), Pac);
        manager.Apply();
        Assert.Equal(Pac, throwing.Value);
        Assert.Equal(RestoreOutcome.Restored, manager.Restore().Outcome);
        _ = (m, store);
    }

    private sealed class ThrowingNotify : ISystemProxyStore
    {
        public string? Value;
        public string? ReadPacUrl() => Value;
        public void WritePacUrl(string url) => Value = url;
        public void DeletePacUrl() => Value = null;
        public void NotifyChanged() => throw new InvalidOperationException("wininet");
    }
}

public class FileStateStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "rigbay-steambypass-tests", Guid.NewGuid().ToString("N"), "state", "proxy-state.json");

    [Fact]
    public void State_round_trips_through_a_file_and_survives_a_new_instance()
    {
        var path = TempFile();
        new FileProxyStateStore(path).Save(new ProxyState("http://127.0.0.1:18081/proxy.pac", "http://old.example/p.pac"));
        var loaded = new FileProxyStateStore(path).Load();
        Assert.Equal(new ProxyState("http://127.0.0.1:18081/proxy.pac", "http://old.example/p.pac"), loaded);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_missing_or_corrupt_file_is_no_state_not_an_exception()
    {
        var path = TempFile();
        var store = new FileProxyStateStore(path);
        Assert.Null(store.Load());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var junk in new[] { "", "not json", "[]", "{}", "{\"Ours\":\"\"}", "\0\0\0" })
        {
            File.WriteAllText(path, junk);
            Assert.Null(store.Load());
        }
    }

    [Fact]
    public void Delete_removes_the_file_and_can_be_repeated()
    {
        var path = TempFile();
        var store = new FileProxyStateStore(path);
        store.Save(new ProxyState("a", null));
        store.Delete();
        store.Delete();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Crash_recovery_works_end_to_end_with_the_real_file_store_and_a_scratch_registry_key()
    {
        using var scratch = new ScratchKey();
        scratch.Store.WritePacUrl("http://old.example/p.pac");
        var statePath = TempFile();
        var state = new FileProxyStateStore(statePath);
        const string pac = "http://127.0.0.1:18081/proxy.pac";

        new SystemProxyManager(scratch.Store, state, pac).Apply();
        Assert.Equal(pac, scratch.Store.ReadPacUrl());
        // ... the process dies here ...
        var recovered = new SystemProxyManager(scratch.Store, new FileProxyStateStore(statePath), pac).Restore();
        Assert.Equal(RestoreOutcome.Restored, recovered.Outcome);
        Assert.Equal("http://old.example/p.pac", scratch.Store.ReadPacUrl());
    }
}


/// <summary>
/// A scratch registry key under HKCU for running the real <see cref="RegistryProxyStore"/> code. Never the real Internet Settings key,
/// and WinINet is never notified. The key is removed when the test ends.
/// </summary>
internal sealed class ScratchKey : IDisposable
{
    private readonly string _path = @"Software\RigbayTests\SteamBypass\" + Guid.NewGuid().ToString("N");

    public ScratchKey()
    {
        Assert.NotEqual(RegistryProxyStore.InternetSettingsKey, _path, StringComparer.OrdinalIgnoreCase);
        Store = new RegistryProxyStore(_path, notifyWinInet: false);
        Assert.False(Store.IsRealSetting);
    }

    public RegistryProxyStore Store { get; }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false); } catch (Exception) { }
        try
        {
            // Remove the parent folders we created, but only if they are empty.
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\RigbayTests\SteamBypass", writable: true);
            if (parent is { SubKeyCount: 0 }) Registry.CurrentUser.DeleteSubKeyTree(@"Software\RigbayTests", throwOnMissingSubKey: false);
        }
        catch (Exception) { }
    }
}

public class RegistryProxyStoreTests
{
    [Fact]
    public void The_real_registry_code_reads_writes_and_deletes_on_a_scratch_key()
    {
        using var k = new ScratchKey();
        Assert.Null(k.Store.ReadPacUrl());              // key does not exist yet
        k.Store.DeletePacUrl();                          // deleting from nothing is fine
        k.Store.WritePacUrl("http://127.0.0.1:18081/proxy.pac");
        Assert.Equal("http://127.0.0.1:18081/proxy.pac", k.Store.ReadPacUrl());
        k.Store.WritePacUrl("http://other.example/p.pac");
        Assert.Equal("http://other.example/p.pac", k.Store.ReadPacUrl());
        k.Store.DeletePacUrl();
        Assert.Null(k.Store.ReadPacUrl());
        k.Store.NotifyChanged();                         // no WinINet call for a scratch key
    }

    [Fact]
    public void The_full_apply_and_restore_run_against_a_scratch_key_and_leaves_the_real_setting_alone()
    {
        string? realBefore = ReadRealSetting();
        using var k = new ScratchKey();
        k.Store.WritePacUrl("http://old.example/p.pac");
        var manager = new SystemProxyManager(k.Store, new MemoryStateStore(), "http://127.0.0.1:18081/proxy.pac");
        manager.Apply();
        Assert.Equal("http://127.0.0.1:18081/proxy.pac", k.Store.ReadPacUrl());
        Assert.True(manager.IsApplied());
        Assert.Equal(RestoreOutcome.Restored, manager.Restore().Outcome);
        Assert.Equal("http://old.example/p.pac", k.Store.ReadPacUrl());
        Assert.Equal(realBefore, ReadRealSetting());   // a read of the real value: unchanged by everything above
    }

    [Fact]
    public void The_production_store_points_at_the_real_key_and_a_scratch_store_does_not()
    {
        Assert.True(new RegistryProxyStore().IsRealSetting);
        Assert.False(new RegistryProxyStore(@"Software\RigbayTests\x", false).IsRealSetting);
    }

    [Fact]
    public void No_test_builds_a_store_on_the_real_key()
    {
        // Constructing the production store is harmless (it touches nothing until used), and only this file and the module's
        // default wiring may do it. A test that wrote through it would change the owner's Windows settings.
        foreach (var file in Directory.EnumerateFiles(DohTests.SourceRoot() + @"\..\..\tests\Rigbay.SteamBypass.Tests", "*.cs"))
        {
            if (Path.GetFileName(file) == "SystemProxyTests.cs") continue;
            Assert.DoesNotContain("new RegistryProxyStore(", File.ReadAllText(file));
            Assert.DoesNotContain("Dependencies.Real()", File.ReadAllText(file));
        }
    }

    private static string? ReadRealSetting()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryProxyStore.InternetSettingsKey, writable: false);
        return key?.GetValue("AutoConfigURL") as string;
    }
}
