using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hermes.App;
using Hermes.App.ViewModels;
using Hermes.Core;
using Microsoft.FSharp.Collections;
using Xunit;

namespace Hermes.Tests.App;

[Trait("Category", "Unit")]
public sealed class ViewModelTests
{
    // ── Helpers ─────────────────────────────────────────────────────

    private static HermesServiceBridge CreateBridge(
        string archiveDir,
        Domain.ChatProviderKind? provider = null)
    {
        var bridge = new HermesServiceBridge();

        var ollama = new Domain.OllamaConfig(
            true, "http://localhost:11434", "nomic-embed-text", "llava", "llama3.2");
        var fallback = new Domain.FallbackConfig("onnx", "none");
        var azure = new Domain.AzureConfig("", "");

        var kind = provider ?? Domain.ChatProviderKind.Ollama;
        var azureOai = kind.IsAzureOpenAI
            ? new Domain.AzureOpenAIConfig(
                "https://test.openai.azure.com", "test-key", "gpt-4o", 4096, 300)
            : new Domain.AzureOpenAIConfig("", "", "gpt-4o", 4096, 300);
        var chat = new Domain.ChatConfig(kind, azureOai);

        var config = new Domain.HermesConfig(
            archiveDir, "",
            FSharpList<Domain.AccountConfig>.Empty,
            15, 20480,
            FSharpList<Domain.WatchFolderConfig>.Empty,
            ollama, fallback, azure, chat);

        SetField(bridge, "_config", config);
        return bridge;
    }

    private static void SetField(object target, string name, object? value)
    {
        var field = target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Field '{name}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hermes-vmtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "unclassified"));
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<Algebra.Database> InitDb(string archiveDir)
    {
        var dbPath = Path.Combine(archiveDir, "db.sqlite");
        var db = Database.fromPath(dbPath);
        await db.initSchema.Invoke(null!);
        return db;
    }

    private static FSharpList<Tuple<string, object>> Params(
        params (string Key, object Value)[] ps) =>
        ListModule.OfSeq(ps.Select(p => Tuple.Create(p.Key, p.Value)));

    private static async Task Exec(
        Algebra.Database db, string sql, params (string Key, object Value)[] ps) =>
        await db.execNonQuery.Invoke(sql).Invoke(Params(ps));

    /// <summary>
    /// Calls the private refresh sub-methods on ShellViewModel, bypassing
    /// RefreshStatusAsync which reloads config from the real config.yaml
    /// and corrupts the test archive directory.
    /// </summary>
    private static async Task RefreshVmSkippingStatusReload(ShellViewModel vm)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(ShellViewModel);

        await InvokePrivateAsync(vm, type, "RefreshOllamaAsync", flags);
        await InvokePrivateAsync(vm, type, "RefreshIndexStatsAsync", flags);
        await InvokePrivateAsync(vm, type, "RefreshPipelineCountsAsync", flags);
        type.GetMethod("RefreshCategories", flags)!.Invoke(vm, null);
        await InvokePrivateAsync(vm, type, "RefreshAccountsAsync", flags);
        await InvokePrivateAsync(vm, type, "RefreshRemindersAsync", flags);
        type.GetMethod("RefreshLastSync", flags)!.Invoke(vm, null);
        type.GetMethod("RefreshStatusBar", flags)!.Invoke(vm, null);
    }

    private static async Task RefreshRemindersOnly(ShellViewModel vm)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        await InvokePrivateAsync(vm, typeof(ShellViewModel), "RefreshRemindersAsync", flags);
    }

    private static async Task InvokePrivateAsync(
        object target, Type type, string method, BindingFlags flags)
    {
        var result = type.GetMethod(method, flags)!.Invoke(target, null);
        if (result is Task t) await t;
    }

    // ── VM-1: Constructor defaults ─────────────────────────────────

    [Fact]
    public void Constructor_SetsExpectedDefaults()
    {
        var tempDir = CreateTempDir();
        try
        {
            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                Assert.False(vm.AiEnabled);
                Assert.True(vm.IsChatPaneVisible);
                Assert.Empty(vm.Messages);
                Assert.Empty(vm.OverdueReminders);
                Assert.Empty(vm.UpcomingReminders);
                Assert.Empty(vm.Categories);
                Assert.Equal("Starting...", vm.StatusBarText);
                Assert.Equal("Last sync: Never", vm.LastSyncText);
                Assert.False(vm.IsSyncing);
                Assert.Null(vm.IndexStats);
                Assert.Null(vm.PipelineCounts);
                Assert.Equal(0, vm.ActionItemCount);
                Assert.Equal(NavigatorMode.ActionItems, vm.ActiveMode);
                Assert.False(vm.IsPaused);
                Assert.Null(vm.CurrentItem);
                Assert.False(vm.CanNavigateBack);
                Assert.Empty(vm.BreadcrumbItems);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-2: RefreshAsync populates pipeline counts ───────────────

    [Fact]
    public async Task RefreshAsync_PopulatesPipelineCounts()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 2 files in unclassified → intake
            var unclassifiedDir = Path.Combine(tempDir, "unclassified");
            await File.WriteAllTextAsync(Path.Combine(unclassifiedDir, "a.txt"), "x");
            await File.WriteAllTextAsync(Path.Combine(unclassifiedDir, "b.txt"), "x");

            var db = await InitDb(tempDir);
            try
            {
                // Document with extracted_at IS NULL → extracting
                await Exec(db,
                    "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES (@s, @p, @c, @h)",
                    ("@s", "manual_drop"), ("@p", "test.pdf"),
                    ("@c", "invoices"), ("@h", "hash1"));

                // Document with category 'unsorted' + extracted_at set → classifying
                await Exec(db,
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_at) VALUES (@s, @p, @c, @h, @e)",
                    ("@s", "manual_drop"), ("@p", "test2.pdf"),
                    ("@c", "unsorted"), ("@h", "hash2"), ("@e", "2024-01-01"));
            }
            finally { db.dispose.Invoke(null!); }

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                await RefreshVmSkippingStatusReload(vm);

                Assert.NotNull(vm.PipelineCounts);
                var pc = vm.PipelineCounts!;
                Assert.Equal(2, pc.IntakeCount);
                // extracting = docs WHERE extracted_at IS NULL (first doc)
                Assert.True(pc.ExtractingCount >= 1,
                    $"Expected ExtractingCount >= 1, got {pc.ExtractingCount}");
                // classifying = docs WHERE category IN ('unsorted','unclassified') AND extracted_at IS NOT NULL
                Assert.True(pc.ClassifyingCount >= 1,
                    $"Expected ClassifyingCount >= 1, got {pc.ClassifyingCount}");
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-3: RefreshAsync populates accounts ──────────────────────

    [Fact]
    public async Task RefreshAsync_PopulatesAccounts()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = await InitDb(tempDir);
            try
            {
                await Exec(db,
                    "INSERT INTO sync_state (account, last_sync_at, message_count) VALUES (@a, @t, @c)",
                    ("@a", "test@gmail.com"), ("@t", "2024-06-15T10:00:00Z"), ("@c", (object)42L));

                await Exec(db,
                    "INSERT INTO sync_state (account, last_sync_at, message_count) VALUES (@a, @t, @c)",
                    ("@a", "work@gmail.com"), ("@t", "2024-06-14T08:00:00Z"), ("@c", (object)100L));
            }
            finally { db.dispose.Invoke(null!); }

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                await RefreshVmSkippingStatusReload(vm);

                Assert.Equal(2, vm.Accounts.Count);
                Assert.Contains(vm.Accounts, a => a.Label == "test@gmail.com");
                Assert.Contains(vm.Accounts, a => a.Label == "work@gmail.com");
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-4: RefreshAsync populates reminders ─────────────────────

    [Fact]
    public async Task RefreshAsync_PopulatesReminders()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = await InitDb(tempDir);
            try
            {
                // Two overdue reminders (past due dates)
                await Exec(db,
                    "INSERT INTO reminders (vendor, amount, due_date, category, status) VALUES (@v, @a, @d, @c, @s)",
                    ("@v", "Electric Co"), ("@a", 150.0),
                    ("@d", "2020-01-01T00:00:00+00:00"), ("@c", "utilities"), ("@s", "active"));

                await Exec(db,
                    "INSERT INTO reminders (vendor, amount, due_date, category, status) VALUES (@v, @a, @d, @c, @s)",
                    ("@v", "Water Corp"), ("@a", 80.0),
                    ("@d", "2023-06-01T00:00:00+00:00"), ("@c", "utilities"), ("@s", "active"));

                // One upcoming reminder (far-future due date)
                await Exec(db,
                    "INSERT INTO reminders (vendor, amount, due_date, category, status) VALUES (@v, @a, @d, @c, @s)",
                    ("@v", "Insurance Ltd"), ("@a", 500.0),
                    ("@d", "2099-12-31T00:00:00+00:00"), ("@c", "insurance"), ("@s", "active"));
            }
            finally { db.dispose.Invoke(null!); }

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                await RefreshVmSkippingStatusReload(vm);

                Assert.Equal(2, vm.OverdueReminders.Count);
                Assert.Single(vm.UpcomingReminders);
                Assert.Equal(3, vm.ActionItemCount);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-5: SendMessageAsync adds messages ───────────────────────

    [Fact]
    public async Task SendMessageAsync_AddsUserAndHermesMessages()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = await InitDb(tempDir);
            try
            {
                // FTS trigger auto-populates documents_fts on INSERT
                await Exec(db,
                    @"INSERT INTO documents
                        (source_type, saved_path, category, sha256, original_name, extracted_text)
                      VALUES (@s, @p, @c, @h, @n, @t)",
                    ("@s", "manual_drop"),
                    ("@p", "insurance/policy.pdf"),
                    ("@c", "insurance"),
                    ("@h", "hash-ins-1"),
                    ("@n", "insurance-policy.pdf"),
                    ("@t", "This is an insurance policy document for home coverage"));
            }
            finally { db.dispose.Invoke(null!); }

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                await vm.SendMessageAsync("insurance");

                // At least: user message + Hermes response
                Assert.True(vm.Messages.Count >= 2,
                    $"Expected >= 2 messages, got {vm.Messages.Count}");
                Assert.Equal("You", vm.Messages[0].Speaker);
                Assert.Equal("insurance", vm.Messages[0].Text);
                Assert.True(vm.Messages[0].IsUser);
                Assert.Equal("Hermes", vm.Messages[1].Speaker);
                Assert.False(vm.Messages[1].IsUser);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-7: MarkPaidAsync removes reminder ───────────────────────

    [Fact]
    public async Task MarkPaidAsync_RemovesOverdueReminder()
    {
        var tempDir = CreateTempDir();
        try
        {
            long reminderId;
            var db = await InitDb(tempDir);
            try
            {
                await Exec(db,
                    "INSERT INTO reminders (vendor, amount, due_date, category, status) VALUES (@v, @a, @d, @c, @s)",
                    ("@v", "Electric Co"), ("@a", 150.0),
                    ("@d", "2020-01-01T00:00:00+00:00"), ("@c", "utilities"), ("@s", "active"));

                var idObj = await db.execScalar
                    .Invoke("SELECT last_insert_rowid()")
                    .Invoke(FSharpList<Tuple<string, object>>.Empty);
                reminderId = (long)idObj!;
            }
            finally { db.dispose.Invoke(null!); }

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                await RefreshVmSkippingStatusReload(vm);
                Assert.Single(vm.OverdueReminders);

                await vm.MarkPaidAsync(reminderId);

                Assert.Empty(vm.OverdueReminders);
                Assert.Equal(0, vm.ActionItemCount);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-8: SnoozeAsync removes reminder ─────────────────────────

    [Fact]
    public async Task SnoozeAsync_RemovesReminderFromBothCollections()
    {
        var tempDir = CreateTempDir();
        try
        {
            long reminderId;
            var db = await InitDb(tempDir);
            try
            {
                await Exec(db,
                    "INSERT INTO reminders (vendor, amount, due_date, category, status) VALUES (@v, @a, @d, @c, @s)",
                    ("@v", "Water Corp"), ("@a", 80.0),
                    ("@d", "2020-06-01T00:00:00+00:00"), ("@c", "utilities"), ("@s", "active"));

                var idObj = await db.execScalar
                    .Invoke("SELECT last_insert_rowid()")
                    .Invoke(FSharpList<Tuple<string, object>>.Empty);
                reminderId = (long)idObj!;
            }
            finally { db.dispose.Invoke(null!); }

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                await RefreshVmSkippingStatusReload(vm);
                Assert.Single(vm.OverdueReminders);

                await vm.SnoozeAsync(reminderId, 7);

                Assert.Empty(vm.OverdueReminders);
                Assert.Empty(vm.UpcomingReminders);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-9: DismissAsync removes reminder ────────────────────────

    [Fact]
    public async Task DismissAsync_RemovesReminderFromBothCollections()
    {
        var tempDir = CreateTempDir();
        try
        {
            long reminderId;
            var db = await InitDb(tempDir);
            try
            {
                await Exec(db,
                    "INSERT INTO reminders (vendor, amount, due_date, category, status) VALUES (@v, @a, @d, @c, @s)",
                    ("@v", "Insurance Ltd"), ("@a", 500.0),
                    ("@d", "2020-03-15T00:00:00+00:00"), ("@c", "insurance"), ("@s", "active"));

                var idObj = await db.execScalar
                    .Invoke("SELECT last_insert_rowid()")
                    .Invoke(FSharpList<Tuple<string, object>>.Empty);
                reminderId = (long)idObj!;
            }
            finally { db.dispose.Invoke(null!); }

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                await RefreshVmSkippingStatusReload(vm);
                Assert.Single(vm.OverdueReminders);

                await vm.DismissAsync(reminderId);

                Assert.Empty(vm.OverdueReminders);
                Assert.Empty(vm.UpcomingReminders);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-10: ToggleChatPane ──────────────────────────────────────

    [Fact]
    public void ToggleChatPane_TogglesVisibility()
    {
        var tempDir = CreateTempDir();
        try
        {
            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                Assert.True(vm.IsChatPaneVisible);

                vm.ToggleChatPane();
                Assert.False(vm.IsChatPaneVisible);

                vm.ToggleChatPane();
                Assert.True(vm.IsChatPaneVisible);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-11: PropertyChanged fires ───────────────────────────────

    [Fact]
    public async Task PropertyChanged_FiresForStatusBarTextDuringRefresh()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = await InitDb(tempDir);
            db.dispose.Invoke(null!);

            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                var firedProperties = new List<string>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is { } name)
                        firedProperties.Add(name);
                };

                await RefreshVmSkippingStatusReload(vm);

                Assert.Contains(
                    nameof(ShellViewModel.StatusBarText),
                    firedProperties);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    // ── VM-12: ChatProviderName reflects config ────────────────────

    [Fact]
    public void ChatProviderName_ContainsOllamaForOllamaConfig()
    {
        var tempDir = CreateTempDir();
        try
        {
            var bridge = CreateBridge(tempDir);
            var vm = new ShellViewModel(bridge);
            try
            {
                Assert.Contains("Ollama", vm.ChatProviderName);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }

    [Fact]
    public void ChatProviderName_ContainsAzureOpenAIForAzureConfig()
    {
        var tempDir = CreateTempDir();
        try
        {
            var bridge = CreateBridge(tempDir, Domain.ChatProviderKind.AzureOpenAI);
            var vm = new ShellViewModel(bridge);
            try
            {
                Assert.Contains("Azure OpenAI", vm.ChatProviderName);
            }
            finally { vm.Dispose(); }
        }
        finally { Cleanup(tempDir); }
    }
}
