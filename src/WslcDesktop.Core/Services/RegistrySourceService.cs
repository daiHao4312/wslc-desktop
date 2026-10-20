using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 镜像源的**业务规则层**：内置站点与自定义源在这里合成同一个视图，所有"能不能"的判断也只在这里。
///
/// 界面只做两件事：把 <see cref="GetAll"/> 画成列表、把用户动作转成这里的一个方法调用。
/// 这样"默认唯一""默认不能停用""内置不能删""删源连带删凭据"只有一份实现，
/// 不会像之前那样散在 code-behind 的 if 分支里。
///
/// 四条硬规则（都有单测）：
/// <list type="number">
/// <item>默认源**必须存在且启用**；存的那个不满足时视图自愈到第一个启用的源，写入时顺手纠回来。</item>
/// <item>默认源**不能停用、不能删除**。</item>
/// <item>**至少保留一个**启用的源（否则下拉是空的，用户只能手填）。</item>
/// <item>内置站点**不可删除**（它是代码里的常量），只能停用；备注也不可改（那是实测结论）。</item>
/// </list>
/// </summary>
public sealed class RegistrySourceService
{
    /// <summary>备注的最大长度。行是两行显示，太长的说明放不进列表，也没必要存。</summary>
    public const int NoteMaxLength = 200;

    private readonly RegistrySourceStore _store;

    public RegistrySourceService(RegistrySourceStore store) => _store = store;

    /// <summary>统一视图：内置（按预设表次序）→ 自定义（按添加次序），并标出默认源。</summary>
    public IReadOnlyList<RegistrySource> GetAll()
        => BuildView(_store.SourcePreferences, _store.StoredDefaultRegistry);

    /// <summary>拉取镜像时下拉里的候选（= 视图里启用的那些，保持同样次序）。</summary>
    public IReadOnlyList<string> EnabledHosts()
        => GetAll().Where(s => s.Enabled).Select(s => s.Host).ToArray();

    /// <summary>当前生效的默认源（视图自愈后的结果，永远指向一个启用的源）。</summary>
    public string EffectiveDefaultHost
        => GetAll().FirstOrDefault(s => s.IsDefault)?.Host ?? RegistryDefaults.Host;

    /// <summary>按主机名找一行（别名算同一条）。</summary>
    public RegistrySource? Find(string? host)
    {
        var canonical = RegistryInput.CanonicalHost(host);
        if (canonical.Length == 0) return null;

        return GetAll().FirstOrDefault(s => RegistryInput.CanonicalHost(s.Host) == canonical);
    }

    /// <summary>
    /// 添加镜像源。<paramref name="username"/> 非空时顺带配凭据。
    ///
    /// 已经存在（内置站点 / 已加过的自定义源）时不重复添加，只把它恢复为启用并返回 <c>Added=false</c>，
    /// 让界面能如实说"本来就在列表里"。
    /// </summary>
    public async Task<RegistrySourceAddResult> AddAsync(
        string host,
        string? note = null,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateHost(host, out var normalized, out var error)) throw new ArgumentException(error, nameof(host));

        var normalizedNote = NormalizeNote(note, out var noteError);
        if (noteError.Length > 0) throw new ArgumentException(noteError, nameof(note));

        var existing = Find(normalized);
        if (existing is not null)
        {
            if (!existing.Enabled) await SetEnabledAsync(existing.Host, enabled: true, cancellationToken).ConfigureAwait(false);
            if (!existing.IsBuiltIn) await UpdateNoteAsync(existing.Host, normalizedNote, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(username))
            {
                await SetCredentialAsync(existing.Host, username!, password ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new RegistrySourceAddResult(Find(normalized)!, Added: false);
        }

        var preferences = new List<RegistrySourcePreference>(_store.SourcePreferences)
        {
            new()
            {
                Host = normalized,
                Note = normalizedNote,
                Enabled = true,
                IsCustom = true
            }
        };

        await PersistAsync(preferences, defaultOverride: null, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(username))
        {
            await SetCredentialAsync(normalized, username!, password ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }

        return new RegistrySourceAddResult(Find(normalized)!, Added: true);
    }

    /// <summary>设为默认源（必须是启用状态）。</summary>
    public async Task SetDefaultAsync(string host, CancellationToken cancellationToken = default)
    {
        var target = Find(host) ?? throw new InvalidOperationException($"找不到镜像源「{host}」，可能已经被删除。");

        if (target.IsDefault) return;

        if (!target.Enabled)
        {
            throw new InvalidOperationException(
                $"「{target.Host}」现在不在下拉里：先勾上「显示」，再把它设为默认。");
        }

        await PersistAsync(_store.SourcePreferences, target.Host, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>是否出现在下拉里。默认源不能关，最后一个启用的源也不能关。</summary>
    public async Task SetEnabledAsync(string host, bool enabled, CancellationToken cancellationToken = default)
    {
        var target = Find(host) ?? throw new InvalidOperationException($"找不到镜像源「{host}」，可能已经被删除。");

        if (target.Enabled == enabled) return;

        if (!enabled)
        {
            if (target.IsDefault)
            {
                throw new InvalidOperationException(
                    $"「{target.Host}」是当前默认镜像源：先把默认换到别的源，再把它从下拉里移除。");
            }

            if (GetAll().Count(s => s.Enabled) <= 1)
            {
                throw new InvalidOperationException("至少要保留一个镜像源在下拉里，否则拉取时只能手填地址。");
            }
        }

        await PersistAsync(Upsert(target, p => p with { Enabled = enabled }), defaultOverride: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>改备注。内置站点的备注是实测结论，不可改。</summary>
    public async Task UpdateNoteAsync(string host, string? note, CancellationToken cancellationToken = default)
    {
        var target = Find(host) ?? throw new InvalidOperationException($"找不到镜像源「{host}」，可能已经被删除。");

        if (target.IsBuiltIn)
        {
            throw new InvalidOperationException($"「{target.Host}」是内置站点，备注是实测结论，不能修改。");
        }

        var normalizedNote = NormalizeNote(note, out var error);
        if (error.Length > 0) throw new ArgumentException(error, nameof(note));

        if (string.Equals(normalizedNote, target.Note, StringComparison.Ordinal)) return;

        await PersistAsync(Upsert(target, p => p with { Note = normalizedNote }), defaultOverride: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 删除自定义镜像源（内置删不掉；默认源也不行）。删除会**连带删掉它的凭据**，不留孤儿记录。
    /// </summary>
    public async Task RemoveAsync(string host, CancellationToken cancellationToken = default)
    {
        var target = Find(host) ?? throw new InvalidOperationException($"找不到镜像源「{host}」，可能已经被删除。");

        if (target.IsBuiltIn)
        {
            throw new InvalidOperationException($"「{target.Host}」是内置站点，不能删除：取消勾选「显示」就能把它从下拉里去掉。");
        }

        if (target.IsDefault)
        {
            throw new InvalidOperationException($"「{target.Host}」是当前默认镜像源：先把默认换到别的源，再删除。");
        }

        var canonical = RegistryInput.CanonicalHost(target.Host);
        var preferences = _store.SourcePreferences
            .Where(p => RegistryInput.CanonicalHost(p.Host) != canonical)
            .ToList();

        await PersistAsync(preferences, defaultOverride: null, cancellationToken).ConfigureAwait(false);
        await ClearCredentialAsync(target.Host, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>配置/更新凭据（内部加密）。源必须已经在列表里 —— 不允许给"看不见的源"配凭据。</summary>
    public async Task SetCredentialAsync(
        string host,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var target = Find(host) ?? throw new InvalidOperationException($"找不到镜像源「{host}」。请先在列表里添加它。");

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("用户名不能为空。多数私有源这里填账号邮箱或令牌名。", nameof(username));
        }

        var canonical = RegistryInput.CanonicalHost(target.Host);
        var credentials = _store.Credentials
            .Where(c => RegistryInput.CanonicalHost(c.Host) != canonical)
            .ToList();

        credentials.Add(RegistrySourceStore.CreateCredential(target.Host, username, password));

        await _store.SaveCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清除凭据（源本身保留）。</summary>
    public async Task ClearCredentialAsync(string host, CancellationToken cancellationToken = default)
    {
        var canonical = RegistryInput.CanonicalHost(host);
        if (canonical.Length == 0) return;

        var credentials = _store.Credentials
            .Where(c => RegistryInput.CanonicalHost(c.Host) != canonical)
            .ToList();

        if (credentials.Count == _store.Credentials.Count) return;

        await _store.SaveCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    // ---------------- 校验（界面直接复用，错误文案只有这一份） ----------------

    /// <summary>校验用户填的主机名，成功时给出归一化后的写法。</summary>
    public static bool TryValidateHost(string? input, out string normalized, out string error)
    {
        normalized = RegistryInput.NormalizeHost(input);
        error = string.Empty;

        if (normalized.Length == 0)
        {
            error = "镜像源地址不能为空。";
            return false;
        }

        if (!RegistryInput.IsValidHost(normalized))
        {
            error = $"镜像源地址不合法：{input}。只填主机名（可带端口），例如 ghcr.io 或 harbor.example.com:5000。";
            return false;
        }

        return true;
    }

    private static string NormalizeNote(string? note, out string error)
    {
        error = string.Empty;
        var text = (note ?? string.Empty).Trim();

        if (text.Length > NoteMaxLength)
        {
            error = $"备注最多 {NoteMaxLength} 个字符（现在 {text.Length} 个）。";
            return string.Empty;
        }

        return text;
    }

    // ---------------- 内部 ----------------

    /// <summary>把偏好合成为视图。**纯函数**：不写盘，默认源非法时只做视图级自愈。</summary>
    private IReadOnlyList<RegistrySource> BuildView(
        IReadOnlyList<RegistrySourcePreference> preferences,
        string storedDefault)
    {
        var result = new List<RegistrySource>(MirrorPresets.Sites.Count + preferences.Count);

        foreach (var site in MirrorPresets.Sites)
        {
            var canonical = RegistryInput.CanonicalHost(site.Host);
            var preference = preferences.FirstOrDefault(p => RegistryInput.CanonicalHost(p.Host) == canonical);

            result.Add(new RegistrySource
            {
                Host = site.Host,
                Note = site.Note,
                IsBuiltIn = true,

                // 没有记录 = 用户没动过它 = 默认启用（这样新增内置站点会自动出现在老用户的列表里）
                Enabled = preference?.Enabled ?? true,
                Username = _store.Find(site.Host)?.Username
            });
        }

        foreach (var preference in preferences)
        {
            if (!preference.IsCustom) continue;

            var canonical = RegistryInput.CanonicalHost(preference.Host);
            if (canonical.Length == 0) continue;

            // 与内置重复（用户当初手填了 docker.io 之类）不另起一行
            if (MirrorPresets.IsPreset(preference.Host)) continue;
            if (result.Any(s => RegistryInput.CanonicalHost(s.Host) == canonical)) continue;

            result.Add(new RegistrySource
            {
                Host = RegistryInput.NormalizeHost(preference.Host),
                Note = preference.Note ?? string.Empty,
                IsBuiltIn = false,
                Enabled = preference.Enabled,
                Username = _store.Find(preference.Host)?.Username
            });
        }

        // 默认标记：存的那个仍在且启用就用它，否则退到第一个启用的源（视图自愈）
        var storedCanonical = RegistryInput.CanonicalHost(storedDefault);
        var index = result.FindIndex(s => s.Enabled && RegistryInput.CanonicalHost(s.Host) == storedCanonical);
        if (index < 0) index = result.FindIndex(s => s.Enabled);

        if (index >= 0) result[index] = result[index] with { IsDefault = true };

        return result;
    }

    private List<RegistrySourcePreference> Upsert(
        RegistrySource target,
        Func<RegistrySourcePreference, RegistrySourcePreference> update)
    {
        var canonical = RegistryInput.CanonicalHost(target.Host);
        var preferences = new List<RegistrySourcePreference>(_store.SourcePreferences);

        var index = preferences.FindIndex(p => RegistryInput.CanonicalHost(p.Host) == canonical);
        if (index >= 0)
        {
            preferences[index] = update(preferences[index]);
        }
        else
        {
            preferences.Add(update(new RegistrySourcePreference
            {
                Host = target.Host,
                IsCustom = !target.IsBuiltIn,
                Enabled = true
            }));
        }

        return preferences;
    }

    /// <summary>
    /// 落盘。**每次都带一个有效的默认源**：默认是列表的派生属性，
    /// 与列表分开写就有机会留下"默认指向一个已经不存在的源"。
    /// </summary>
    private async Task PersistAsync(
        IReadOnlyList<RegistrySourcePreference> preferences,
        string? defaultOverride,
        CancellationToken cancellationToken)
    {
        var view = BuildView(preferences, defaultOverride ?? _store.StoredDefaultRegistry);
        var effectiveDefault = view.FirstOrDefault(s => s.IsDefault)?.Host
                               ?? view.FirstOrDefault(s => s.Enabled)?.Host
                               ?? RegistryDefaults.Host;

        await _store.SaveSourcesAsync(preferences, effectiveDefault, cancellationToken).ConfigureAwait(false);
    }
}
