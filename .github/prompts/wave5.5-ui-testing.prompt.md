---
description: "Wave 5.5: Comprehensive UI testing — ViewModel tests, Avalonia.Headless tests, and manual smoke test checklist."
---

# Wave 5.5: UI Testing — ViewModel + Headless + Smoke

> **Wave status**: `.project/waves/wave-5.5-ui-testing.md`  
> **Design reference**: [15-rich-ui.md](../../.project/design/15-rich-ui.md) (pipeline funnel)

**Branch**: `feat/ui-testing`

**IMPORTANT: Use a git worktree.**
```
cd c:\work\hermes
git worktree add ..\hermes-uitest feat/ui-testing 2>/dev/null || git worktree add ..\hermes-uitest -b feat/ui-testing
cd c:\work\hermes-uitest
```

**Rules**:
- Use `@csharp-dev` for all C# test code
- Build + test after each section: `dotnet build hermes.slnx --nologo && dotnet test --nologo --no-build`
- 755 tests current baseline — must stay green throughout

---

## Part 1: ViewModel Tests (highest value — no Avalonia dependency)

Create `tests/Hermes.Tests.App/ViewModelTests.cs` in a **new C# test project** (`Hermes.Tests.App.csproj`) that references `Hermes.App` and `Hermes.Core` but NOT Avalonia directly.

**Why a separate project**: ViewModel tests need C# (testing C# code), but the main test project is F#. Keep them separate.

### Project setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Hermes.App\Hermes.App.csproj" />
    <ProjectReference Include="..\..\src\Hermes.Core\Hermes.Core.fsproj" />
  </ItemGroup>
</Project>
```

Add to `hermes.slnx`.

### Tests to write

**VM-1: ShellViewModel initialises with defaults**
```csharp
[Fact]
public void ShellViewModel_Constructor_InitialisesWithDefaults()
{
    var bridge = CreateFakeBridge();
    var vm = new ShellViewModel(bridge);
    
    Assert.False(vm.AiEnabled);
    Assert.True(vm.IsChatPaneVisible);
    Assert.Equal(0, vm.IntakeCount);
    Assert.Equal(0, vm.ExtractingCount);
    Assert.Equal(0, vm.ClassifyingCount);
    Assert.Equal(0, vm.LibraryCount);
    Assert.Empty(vm.Messages);
    Assert.Empty(vm.OverdueReminders);
    Assert.Empty(vm.UpcomingReminders);
    Assert.Empty(vm.Categories);
}
```

**VM-2: RefreshAsync populates pipeline counts**
- Create fake bridge with a real in-memory DB (insert test documents at various pipeline stages)
- Call `vm.RefreshAsync()`
- Assert: `IntakeCount`, `ExtractingCount`, `ClassifyingCount`, `LibraryCount` match inserted data
- Assert: `Categories` populated with correct counts
- Assert: `StatusBarText` contains document count

**VM-3: RefreshAsync populates accounts**
- DB has sync_state entries
- After refresh: `Accounts` list has correct labels and message counts

**VM-4: RefreshAsync populates reminders**
- DB has active reminders (some overdue, some upcoming)
- After refresh: `OverdueReminders` and `UpcomingReminders` populated correctly
- `ActionItemCount` equals total

**VM-5: SendMessageAsync adds messages**
- Set up DB with test documents matching a query
- Call `vm.SendMessageAsync("test query")`
- Assert: Messages collection has 2 entries (user message + Hermes response)
- Assert: Response has document cards

**VM-6: SendMessageAsync with AI enabled uses ChatProvider**
- Provide a fake ChatProvider that returns canned summary
- `vm.AiEnabled = true`
- Call `vm.SendMessageAsync("query")`
- Assert: AI summary appears in response message

**VM-7: MarkPaidAsync removes from active reminders**
- Insert reminder → refresh → `OverdueReminders.Count == 1`
- Call `vm.MarkPaidAsync(reminderId)`
- Assert: `OverdueReminders.Count == 0`

**VM-8: SnoozeAsync removes from active reminders**
- Insert reminder → refresh → visible
- Call `vm.SnoozeAsync(reminderId, 7)`
- Assert: not in OverdueReminders or UpcomingReminders

**VM-9: DismissAsync removes permanently**
- Insert → dismiss → not in any collection

**VM-10: ToggleChatPane flips visibility**
```csharp
Assert.True(vm.IsChatPaneVisible);
vm.ToggleChatPane();
Assert.False(vm.IsChatPaneVisible);
vm.ToggleChatPane();
Assert.True(vm.IsChatPaneVisible);
```

**VM-11: PropertyChanged fires for all observable properties**
- Subscribe to PropertyChanged
- Call RefreshAsync
- Assert: events fired for OllamaStatus, IndexStats, Categories, Accounts, etc.

**VM-12: ChatProviderName reflects config**
- Bridge config set to AzureOpenAI → `ChatProviderName` contains "Azure OpenAI"
- Bridge config set to Ollama → `ChatProviderName` contains "Ollama"

### Test helper: FakeBridge

Create a `FakeHermesServiceBridge` that wraps an in-memory DB:

```csharp
private static HermesServiceBridge CreateFakeBridge(string? archiveDir = null)
{
    var dir = archiveDir ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(dir);
    Directory.CreateDirectory(Path.Combine(dir, "unclassified"));
    
    var dbPath = Path.Combine(dir, "db.sqlite");
    var db = Database.fromPath(dbPath);
    db.initSchema.Invoke(null!).Wait();
    
    // Bridge needs Config — create a minimal one
    // ... set up bridge with test config pointing to temp dir
    
    return bridge;
}
```

---

## Part 2: Avalonia.Headless Tests (control wiring verification)

Create `tests/Hermes.Tests.UI/HeadlessTests.cs` in another test project with Avalonia.Headless dependency.

### Project setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia.Headless.XUnit" Version="11.2.8" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Hermes.App\Hermes.App.csproj" />
  </ItemGroup>
</Project>
```

### Tests to write

**HL-1: ShellWindow creates with all funnel sections**
```csharp
[AvaloniaFact]
public void ShellWindow_HasAllFunnelSections()
{
    var window = new ShellWindow(CreateFakeBridge());
    window.Show();
    
    Assert.NotNull(window.FindControl<Expander>("SourcesExpander"));
    Assert.NotNull(window.FindControl<Expander>("IntakeExpander"));
    Assert.NotNull(window.FindControl<Expander>("ExtractingExpander"));
    Assert.NotNull(window.FindControl<Expander>("ClassifyingExpander"));
    Assert.NotNull(window.FindControl<Expander>("LibraryExpander"));
    Assert.NotNull(window.FindControl<Expander>("IndexExpander"));
    Assert.NotNull(window.FindControl<Expander>("ActionItemsExpander"));
    Assert.NotNull(window.FindControl<Expander>("ServicesExpander"));
}
```

**HL-2: Three-column layout exists**
```csharp
[AvaloniaFact]
public void ShellWindow_HasThreeColumns()
{
    var window = new ShellWindow(CreateFakeBridge());
    window.Show();
    
    Assert.NotNull(window.FindControl<ScrollViewer>("FunnelScroller"));
    Assert.NotNull(window.FindControl<ScrollViewer>("ContentScroller") 
        ?? window.FindControl<ScrollViewer>("ChatScroller"));
    // Chat pane
    Assert.NotNull(window.FindControl<TextBox>("ChatInput"));
    Assert.NotNull(window.FindControl<Button>("SendButton"));
}
```

**HL-3: Chat input and send button work**
```csharp
[AvaloniaFact]
public async Task ShellWindow_ChatSend_AddsMessage()
{
    var window = new ShellWindow(CreateFakeBridge());
    window.Show();
    
    var input = window.FindControl<TextBox>("ChatInput")!;
    input.Text = "test query";
    
    var send = window.FindControl<Button>("SendButton")!;
    send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    
    // After send, input should be cleared
    Assert.Equal("", input.Text);
}
```

**HL-4: AI toggle changes state**
```csharp
[AvaloniaFact]
public void ShellWindow_AiToggle_ChangesState()
{
    var window = new ShellWindow(CreateFakeBridge());
    window.Show();
    
    var toggle = window.FindControl<ToggleButton>("AiToggle")!;
    Assert.False(toggle.IsChecked);
    
    toggle.IsChecked = true;
    // VM should reflect
}
```

**HL-5: Settings button opens dialog**
```csharp
[AvaloniaFact]
public void ShellWindow_SettingsButton_Exists()
{
    var window = new ShellWindow(CreateFakeBridge());
    window.Show();
    
    var settings = window.FindControl<Button>("SettingsButton");
    Assert.NotNull(settings);
}
```

**HL-6: Suggested query chips exist**
```csharp
[AvaloniaFact]
public void ShellWindow_SuggestedQueryChips_Present()
{
    var window = new ShellWindow(CreateFakeBridge());
    window.Show();
    
    var chips = window.FindControl<WrapPanel>("SuggestedQueries");
    Assert.NotNull(chips);
    Assert.True(chips.Children.Count >= 3); // at least some chips
}
```

**HL-7: Processing sections hidden when empty**
```csharp
[AvaloniaFact]
public void ShellWindow_EmptyPipeline_ProcessingSectionsCollapsed()
{
    var window = new ShellWindow(CreateFakeBridge()); // empty DB
    window.Show();
    
    // Pipeline counts are 0, processing sections should auto-collapse or hide
    var intake = window.FindControl<Expander>("IntakeExpander");
    var extracting = window.FindControl<Expander>("ExtractingExpander");
    var classifying = window.FindControl<Expander>("ClassifyingExpander");
    
    // These should be collapsed or not visible when count = 0
    // (depends on implementation — check IsExpanded or IsVisible)
}
```

**HL-8: Chat pane toggle works**
```csharp
[AvaloniaFact]
public void ShellWindow_ChatToggle_HidesPane()
{
    var window = new ShellWindow(CreateFakeBridge());
    window.Show();
    
    // Find chat toggle button and click it
    // Assert chat pane visibility changed
}
```

---

## Part 3: Manual Smoke Test Checklist

Create `.github/prompts/smoke-test.prompt.md` with a human-walkthrough checklist.

### Pre-conditions
- Hermes running: `dotnet run --project src/Hermes.App`
- At least 1 Gmail account configured
- Archive has documents (2,163+)
- Ollama running (or Azure OpenAI configured)

### Checklist

**Launch & Layout**
- [ ] Window opens in ~3 seconds
- [ ] Three columns visible: funnel (left), content (centre), chat (right)
- [ ] Funnel shows all 8 sections: Sources, Intake, Extracting, Classifying, Library, Index, Action Items, Services
- [ ] Status bar at bottom shows document count + state

**Sources Section**
- [ ] Email accounts listed with message counts
- [ ] Backfill progress bar visible (if backfill active)
- [ ] "Last sync: Nm ago" updates on refresh
- [ ] [+ Add Account] button opens OAuth dialog
- [ ] [+ Add Folder] button opens folder picker

**Processing Pipeline**
- [ ] Intake section shows count (or collapses when 0)
- [ ] Drop a PDF into ~/Downloads → appears in Intake within 15 seconds
- [ ] After sync cycle: file moves from Intake → Extracting → Classifying → Library

**Library**
- [ ] Categories listed with correct counts
- [ ] Click "invoices" → content pane shows document list
- [ ] Click a document → content pane shows metadata + extracted markdown preview
- [ ] [Open File] button opens the PDF in default app
- [ ] Classification tier + confidence shown ("content (85%)" or "rule")

**Index**
- [ ] Progress bars show searchable/embedded counts
- [ ] Percentages update after sync cycle

**Action Items**
- [ ] Overdue reminders shown in red
- [ ] Upcoming reminders shown in amber
- [ ] [Mark Paid] → moves to completed
- [ ] [Snooze 7d] → disappears (reappears after snooze)
- [ ] [Dismiss] → permanently removed
- [ ] Document link on reminder → opens file

**Chat**
- [ ] Type query → results appear with document cards
- [ ] Document cards show filename, category, date, amount
- [ ] Click document card → opens file in default app
- [ ] AI toggle → enables LLM summarisation
- [ ] With AI on: response includes natural language summary + document cards
- [ ] Suggested query chips visible when chat is empty
- [ ] Click chip → query executes

**Chat Pane Toggle**
- [ ] Click 💬 → chat pane hides
- [ ] Click 💬 again → chat pane reappears
- [ ] Chat history preserved across toggle

**Settings**
- [ ] ⚙ button opens settings dialog
- [ ] General: sync interval, min attachment size populated from config
- [ ] AI/Chat: provider radio (Ollama/Azure OpenAI) reflects current config
- [ ] Accounts: listed with backfill toggle + batch size
- [ ] Save → closes dialog, changes take effect

**Sync**
- [ ] [Sync Now] → button shows "Syncing...", completes, counts update
- [ ] [Pause] → toggles to "Resume", sync stops
- [ ] Resume → sync restarts

**Error States**
- [ ] Ollama not running → service dot shows red, chat works without AI
- [ ] No Gmail credentials → "Add Account" shown in Sources
- [ ] Empty archive → Library shows 0, processing sections empty, chat says "No database found"

---

## Merge Gate

- All ViewModel tests pass (VM-1 through VM-12)
- All Headless tests pass (HL-1 through HL-8)
- Manual smoke test checklist completed with all items ✓
- Build: 0 errors
- Existing 755 tests still pass

```
git push -u origin feat/ui-testing
```

Do NOT merge — await review.
