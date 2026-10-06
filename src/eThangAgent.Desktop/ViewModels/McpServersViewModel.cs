using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One row of the MCP servers dialog's list: the config row's fields plus
///     the pooled access's live state (issue #106). Display fields only - the form
///     edits a snapshot, the store persists it.</summary>
internal sealed record McpServerRow(
    int Id,
    string Name,
    McpApprovalState Approval,
    McpTransport Transport,
    string CommandOrUrl,
    McpConnectionState State,
    string? Error,
    string? Stderr)
{
  public string StateDisplay => State switch
  {
    McpConnectionState.NotConnected => "not connected",
    McpConnectionState.Connected => "connected",
    McpConnectionState.Failed => "failed",
    _ => "unknown",
  };

  public string ApprovalDisplay => Approval switch
  {
    McpApprovalState.Pending => "pending",
    McpApprovalState.Approved => "approved",
    McpApprovalState.Revoked => "revoked",
    _ => "unknown",
  };

  public string TransportDisplay => Transport == McpTransport.Stdio ? "stdio" : "http";

  public string Detail => $"{TransportDisplay} - {CommandOrUrl} - {StateDisplay}";
}

/// <summary>View-model behind the MCP servers dialog (issue #106): the configured
///     servers' list with live status (from the session's SHARED pooled access), a
///     config form (name, transport, command or URL, args/env/headers JSON, scope,
///     pinned version) writing through the V15 store, the approval controls (the
///     trust event: approve, revoke), and removal (tokens deleted with the row).
///     Editing a server's identity (name, transport, command or URL) re-opens
///     approval - the trust decision was about THAT command, and the row must not
///     stay approved over a changed one. Pure state and commands; window mechanics
///     belong to the view. Guards fail with structured error text, never exceptions.
///     The status refresh reads the access's listing, which never connects anything.
///     No status updates are pushed (the pool is lazy); the dialog refreshes on open
///     and on demand.</summary>
internal sealed partial class McpServersViewModel : ObservableObject
{
  private readonly IMcpServerStore _store;
  private readonly IMcpServerAccess _access;
  private readonly string _workspaceId;

  public IRelayCommand SaveCommand { get; }

  public IRelayCommand RemoveCommand { get; }

  public IRelayCommand ApproveCommand { get; }

  public IRelayCommand RevokeCommand { get; }

  public IRelayCommand RefreshCommand { get; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  [ObservableProperty]
  public partial string? LoadError { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<McpServerRow> Servers { get; set; } = [];

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
  [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
  [NotifyCanExecuteChangedFor(nameof(RevokeCommand))]
  public partial McpServerRow? Selected { get; set; }

  // ---- form state (a snapshot of the selected row, or a blank add) ----

  /// <summary>The row being edited, or null when the form adds a new server.</summary>
  public McpServerRow? Editing { get; private set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial string FormName { get; set; } = string.Empty;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial int FormTransportIndex { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial string FormCommandOrUrl { get; set; } = string.Empty;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial string FormArgsJson { get; set; } = "[]";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial string FormEnvJson { get; set; } = "{}";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial string FormHeadersJson { get; set; } = "{}";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial int FormScopeIndex { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FormError))]
  [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
  public partial string FormPinnedVersion { get; set; } = string.Empty;

  [ObservableProperty]
  public partial string? FormError { get; set; }

  [ObservableProperty]
  public partial string? ActionError { get; set; }

  public bool IsFormOpen { get; private set; }

  public McpServersViewModel(IMcpServerStore store, IMcpServerAccess access, string workspaceId)
  {
    _store = store ?? throw new ArgumentNullException(nameof(store));
    _access = access ?? throw new ArgumentNullException(nameof(access));
    _workspaceId = string.IsNullOrWhiteSpace(workspaceId)
        ? throw new ArgumentException("Workspace id must be non-empty.", nameof(workspaceId))
        : workspaceId;

    // Commands exist before the observable properties: setting those raises the
    // changed hooks, which requery command availability. Guards in the actions are
    // load-bearing: ICommand.Execute does not consult CanExecute.
    SaveCommand = new RelayCommand(Save, () => IsFormOpen && FormErrorText is null);
    RemoveCommand = new RelayCommand(Remove, () => Selected is not null);
    ApproveCommand = new RelayCommand(() => SetApproval(McpApprovalState.Approved), () => Selected is not null);
    RevokeCommand = new RelayCommand(() => SetApproval(McpApprovalState.Revoked), () => Selected is not null);
    RefreshCommand = new RelayCommand(() => _ = LoadAsync());
  }

  /// <summary>Fetches the configured rows and the pooled access's live listing
  ///     (never connects) off the UI thread and fills the list. A failure lands in
  ///     <see cref="LoadError"/>; the dialog stays open.</summary>
  public async Task LoadAsync()
  {
    if (IsLoading)
    {
      return;
    }

    IsLoading = true;
    // Named decision (CA1031): a loader fault must land in the dialog's error state,
    // never escape - the fire-and-forget refresh cannot observe it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Task<Result<IReadOnlyList<McpServerConfig>>> storeTask = Task.Run(
          () => _store.ListAsync(_workspaceId, CancellationToken.None));
      Task<McpOutcome> statusTask = Task.Run(
          () => _access.ExecuteAsync(new McpCommand.ListServers(), CancellationToken.None));
      Result<IReadOnlyList<McpServerConfig>> configs = await storeTask.ConfigureAwait(true);
      if (!configs.IsSuccess)
      {
        LoadError = configs.Error.Message;
        return;
      }

      McpOutcome outcome = await statusTask.ConfigureAwait(true);
      Dictionary<string, McpServerStatus> live = outcome is McpOutcome.Status status
          ? status.Servers.ToDictionary(s => s.Name, StringComparer.Ordinal)
          : [];

      Servers = [.. configs.Value.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c =>
      {
        McpServerStatus statusRow = live.GetValueOrDefault(c.Name,
            new McpServerStatus(c.Name, c.ApprovalState, c.Transport, c.CommandOrUrl,
                McpConnectionState.NotConnected, [], null, null));
        return new McpServerRow(c.Id, c.Name, c.ApprovalState, c.Transport, c.CommandOrUrl,
            statusRow.State, statusRow.Error, statusRow.Stderr);
      })];
      LoadError = null;
    }
    catch (Exception ex)
    {
      LoadError = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
#pragma warning restore CA1031
  }

  /// <summary>Opens the form blank for a new server (global scope by default).</summary>
  public void BeginAdd()
  {
    Editing = null;
    IsFormOpen = true;
    FormName = string.Empty;
    FormTransportIndex = 0;
    FormCommandOrUrl = string.Empty;
    FormArgsJson = "[]";
    FormEnvJson = "{}";
    FormHeadersJson = "{}";
    FormScopeIndex = 0;
    FormPinnedVersion = string.Empty;
    FormError = null;
    ActionError = null;
  }

  /// <summary>Closes the form without saving (the Cancel button).</summary>
  public void CloseForm()
  {
    IsFormOpen = false;
    Editing = null;
    FormError = null;
  }

  /// <summary>Opens the form prefilled with the selected row for editing.</summary>
  public void BeginEdit(McpServerRow row)
  {
    Editing = row;
    IsFormOpen = true;
    FormName = row.Name;
    FormTransportIndex = row.Transport == McpTransport.Stdio ? 0 : 1;
    FormCommandOrUrl = row.CommandOrUrl;
    FormArgsJson = LoadJson(row.Id, "args") ?? "[]";
    FormEnvJson = LoadJson(row.Id, "env") ?? "{}";
    FormHeadersJson = LoadJson(row.Id, "headers") ?? "{}";
    FormScopeIndex = 0;
    FormPinnedVersion = string.Empty;
    FormError = null;
    ActionError = null;
  }

  /// <summary>The stored JSON field for one row (args/env/headers) - the form
  ///     prefills from the store, not the row record (which carries none).</summary>
  private string? LoadJson(int id, string field)
  {
    // Named decision (CA1031): a store fault degrades to the empty default - the
    // form still opens, the user can re-enter the JSON.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<McpServerConfig> row = _store.GetAsync(id, _workspaceId).GetAwaiter().GetResult();
      return row.IsSuccess ? field switch
      {
        "args" => row.Value.ArgsJson,
        "env" => row.Value.EnvJson,
        "headers" => row.Value.HeadersJson,
        _ => null,
      } : null;
    }
    catch (Exception)
    {
      return null;
    }
#pragma warning restore CA1031
  }

  /// <summary>The form's first validation problem, or null when clean. Strict:
  ///     required fields required, JSON shapes exact (args an array, env/headers
  ///     objects), HTTP transport an absolute URI.</summary>
  public string? FormErrorText => FirstFormProblem();

  /// <summary>The form's first validation problem, or null when clean. Each check
  ///     is its own statement - no nested ternaries (S3358).</summary>
  private string? FirstFormProblem()
  {
    if (string.IsNullOrWhiteSpace(FormName))
    {
      return "A server name is required.";
    }

    if (string.IsNullOrWhiteSpace(FormCommandOrUrl))
    {
      return CommandRequiredMessage(FormTransportIndex);
    }

    string? endpointProblem = HttpEndpointInvalid(FormTransportIndex, FormCommandOrUrl)
        ? "The HTTP endpoint must be an absolute URL."
        : null;
    string? argsProblem = ValidateJson(FormArgsJson, "Arguments", mustBeArray: true);
    string? envProblem = ValidateJson(FormEnvJson, "Environment", mustBeArray: false);
    string? headersProblem = ValidateJson(FormHeadersJson, "Headers", mustBeArray: false);
    return endpointProblem
        ?? argsProblem
        ?? envProblem
        ?? headersProblem;
  }

  /// <summary>The command-or-URL required message, per transport.</summary>
  private static string CommandRequiredMessage(int transportIndex) => transportIndex == 0
      ? "A stdio server needs its launch command."
      : "An HTTP server needs its endpoint URL.";

  private static string? ValidateJson(string text, string label, bool mustBeArray)
  {
    JsonDocument document;
    string empty = mustBeArray ? "[]" : "{}";
    string candidate = string.IsNullOrWhiteSpace(text) ? empty : text;
    try
    {
      document = JsonDocument.Parse(candidate);
    }
    catch (JsonException ex)
    {
      return $"{label} JSON is malformed: {ex.Message}";
    }

    using (document)
    {
      bool isArray = document.RootElement.ValueKind == JsonValueKind.Array;
      bool isObject = document.RootElement.ValueKind == JsonValueKind.Object;
      return (mustBeArray, isArray, isObject) switch
      {
        (true, false, _) => $"{label} JSON must be an array.",
        (false, _, false) => $"{label} JSON must be an object.",
        _ => null,
      };
    }
  }

  /// <summary>Saves the form: adds a new row (pending - the trust event is a
  ///     separate, deliberate click) or updates the edited one. Editing a server's
  ///     IDENTITY (name, transport, command or URL) re-opens approval: the trust
  ///     decision was about THAT command. A store failure lands in FormError.</summary>
  public void Save()
  {
    string? problem = FormErrorText;
    FormError = problem;
    if (FormError is not null)
    {
      return;
    }

    McpTransport transport = FormTransportIndex == 0 ? McpTransport.Stdio : McpTransport.Http;
    // Named decision (CA1031): a store fault lands in the form's error state,
    // never escapes - the dialog stays open and the user can retry.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      if (Editing is { } editing)
      {
        Result<McpServerConfig> current = _store.GetAsync(editing.Id, _workspaceId).GetAwaiter().GetResult();
        if (!current.IsSuccess)
        {
          FormError = current.Error.Message;
          return;
        }

        bool identityChanged = !string.Equals(current.Value.Name, FormName.Trim(), StringComparison.Ordinal)
            || current.Value.Transport != transport
            || !string.Equals(current.Value.CommandOrUrl, FormCommandOrUrl.Trim(), StringComparison.Ordinal);
        McpApprovalState approval = identityChanged
            ? McpApprovalState.Pending // the trust decision was about the OLD command
            : current.Value.ApprovalState;
        McpServerConfig updated = current.Value with
        {
          Name = FormName.Trim(),
          Transport = transport,
          CommandOrUrl = FormCommandOrUrl.Trim(),
          ArgsJson = NormalizeJson(FormArgsJson, "[]"),
          EnvJson = NormalizeJson(FormEnvJson, "{}"),
          HeadersJson = NormalizeJson(FormHeadersJson, "{}"),
          ApprovalState = approval,
          PinnedVersion = string.IsNullOrWhiteSpace(FormPinnedVersion) ? null : FormPinnedVersion.Trim(),
        };
        Result<McpServerConfig> saved = _store.UpdateAsync(updated).GetAwaiter().GetResult();
        FormError = saved.IsSuccess ? null : saved.Error.Message;
      }
      else
      {
        McpServerConfig created = new(
            0,
            FormName.Trim(),
            transport,
            FormCommandOrUrl.Trim(),
            NormalizeJson(FormArgsJson, "[]"),
            NormalizeJson(FormEnvJson, "{}"),
            NormalizeJson(FormHeadersJson, "{}"),
            FormScopeIndex == 1 ? null : _workspaceId, // 1 = global; 0 = this workspace
            McpApprovalState.Pending, // the trust event is a separate, deliberate click
            string.IsNullOrWhiteSpace(FormPinnedVersion) ? null : FormPinnedVersion.Trim(),
            DateTimeOffset.UtcNow);
        Result<McpServerConfig> saved = _store.AddAsync(created).GetAwaiter().GetResult();
        FormError = saved.IsSuccess ? null : saved.Error.Message;
      }

      if (FormError is null)
      {
        IsFormOpen = false;
        Editing = null;
        _ = LoadAsync();
      }
    }
    catch (Exception ex)
    {
      FormError = ex.Message;
    }
#pragma warning restore CA1031
  }

  private static string NormalizeJson(string text, string empty)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return empty;
    }

    using JsonDocument document = JsonDocument.Parse(text);
    return document.RootElement.GetRawText();
  }

  /// <summary>THE trust event: approves the selected server - it may now connect on
  ///     its first dispatch. A store failure lands in ActionError.</summary>
  private void SetApproval(McpApprovalState state)
  {
    if (Selected is not { } row)
    {
      return;
    }

    // Named decision (CA1031): a store fault lands in the dialog's error state.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<McpServerConfig> current = _store.GetAsync(row.Id, _workspaceId).GetAwaiter().GetResult();
      if (!current.IsSuccess)
      {
        ActionError = current.Error.Message;
        return;
      }

      Result<McpServerConfig> saved = _store.UpdateAsync(
          current.Value with { ApprovalState = state }).GetAwaiter().GetResult();
      ActionError = saved.IsSuccess ? null : saved.Error.Message;
      if (saved.IsSuccess)
      {
        _ = LoadAsync();
      }
    }
    catch (Exception ex)
    {
      ActionError = ex.Message;
    }
#pragma warning restore CA1031
  }

  /// <summary>Removes the selected server: the store deletes its token rows in the
  ///     same transaction (the tables' contract). A failure lands in ActionError.</summary>
  private void Remove()
  {
    if (Selected is not { } row)
    {
      return;
    }

    // Named decision (CA1031): a store fault lands in the dialog's error state.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<bool> deleted = _store.DeleteAsync(row.Id, _workspaceId).GetAwaiter().GetResult();
      ActionError = RemoveMessage(deleted);

      Selected = null;
      _ = LoadAsync();
    }
    catch (Exception ex)
    {
      ActionError = ex.Message;
    }
#pragma warning restore CA1031
  }

  /// <summary>Whether the form's HTTP endpoint fails the absolute-URL check
  ///     (only the http transport is checked; stdio commands are not URLs).</summary>
  private static bool HttpEndpointInvalid(int transportIndex, string commandOrUrl) =>
      transportIndex == 1 && !Uri.IsWellFormedUriString(commandOrUrl, UriKind.Absolute);

  /// <summary>The removal outcome's message: null on a real delete, an
  ///     already-gone note when the row was missing, the store's error otherwise.</summary>
  private static string? RemoveMessage(Result<bool> deleted) => deleted switch
  {
    { IsSuccess: true, Value: true } => null,
    { IsSuccess: true } => "The server was already gone.",
    _ => deleted.Error?.Message ?? "the server could not be removed.",
  };
}
