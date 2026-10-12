"""Integrations: the user's own services (Jellyfin, Seerr, Paperless…), one module each.

A module defines:
  TITLE    shown in Settings and in the AI's hints
  ABOUT    one line for Settings: what it lets you say
  EXAMPLE  an example address for the Address field (http://<server>:8096)
  KEY_HELP where the key is, in words;  KEY_PAGE: that page's path on the server, for the "Where's my key?" button
  FIELDS   [(key, label, secret?)] it needs; secrets go to Windows Credential Manager, the rest to integrations.json
  WORDS    a regex: the AI only hears about the integration when the request matches it, so unused ones cost nothing
  HELP     one line for the AI; {CMD} becomes the command to run it
  RISKY    {subcommand: question}: those need the user's Allow in safe mode
  run(cfg, args) -> str      cfg holds every field, secrets included; never print them
  MCP(cfg) -> dict           optional: an HTTP MCP server Claude Code connects to for matching requests (Home Assistant)
  mcp_risky(tool, args) -> str   optional: a question for MCP tool calls that need an Allow

The AI runs them as:  atticus --int <name> <subcommand> [args…]
Setup (Settings, or by hand):  atticus --int <name> setup  then one "key=value" per line on stdin.
"""
import importlib, json, os, pkgutil, re, sys, urllib.error, urllib.parse, urllib.request

CONFIG = None   # set by atticus.py: <DIR>/integrations.json
_mods = {}


def modules():
    if not _mods:
        for m in pkgutil.iter_modules(__path__):
            if not m.name.startswith('_'):
                try:
                    _mods[m.name] = importlib.import_module(f'{__name__}.{m.name}')
                except Exception as e:   # one broken integration must not take the others down
                    print(f'integration {m.name} failed to load: {e}', file=sys.stderr)
    return _mods


def _saved():
    try:
        return json.load(open(CONFIG, encoding='utf-8'))
    except (OSError, ValueError, TypeError):
        return {}


def config(name, secrets=True):
    """Every field of one integration ({} if it was never set up)."""
    import winctl
    mod, saved = modules().get(name), _saved().get(name)
    if not mod or not saved:
        return {}
    cfg = dict(saved)
    if secrets:
        for k, _, secret in mod.FIELDS:
            if secret:
                cfg[k] = winctl.secret_get(f'{name}/{k}')
    return cfg


def configured():
    """Set up and usable: saved, not turned off, and holding at least one of its keys (an address alone is not enough)."""
    import winctl
    saved, out = _saved(), []
    for n, mod in modules().items():
        if not saved.get(n) or not saved[n].get('enabled', True):
            continue
        secrets = [k for k, _, sec in mod.FIELDS if sec]
        if not secrets or any(winctl.secret_get(f'{n}/{k}') for k in secrets):
            out.append(n)
    return out


def setup(name, values):
    """values: {field: value}. Empty secret = keep the saved one."""
    import winctl
    mod = modules().get(name)
    if not mod:
        return f'no integration called {name}. There are: ' + ', '.join(modules())
    allc = _saved()
    cur = allc.get(name, {})
    for k, _, secret in mod.FIELDS:
        v = values.get(k, '').strip()
        if secret:
            if v:
                winctl.secret_set(f'{name}/{k}', v)
        elif v or k not in cur:
            cur[k] = v.rstrip('/') if k.endswith('url') else v
    cur.setdefault('enabled', True)
    allc[name] = cur
    os.makedirs(os.path.dirname(CONFIG), exist_ok=True)
    json.dump(allc, open(CONFIG, 'w', encoding='utf-8'), indent=1)
    test = getattr(mod, 'check', None)
    return f'{mod.TITLE} saved.' + (' ' + test(config(name)) if test else '')


def remove(name):
    import winctl
    mod, allc = modules().get(name), _saved()
    if mod:
        for k, _, secret in mod.FIELDS:
            if secret:
                winctl.secret_set(f'{name}/{k}', '')
    allc.pop(name, None)
    json.dump(allc, open(CONFIG, 'w', encoding='utf-8'), indent=1)
    return f'{mod.TITLE if mod else name} removed.'


def catalog():
    """Every integration, for Settings → Homelab (never a secret's value)."""
    out = []
    for n, mod in sorted(modules().items(), key=lambda x: x[1].TITLE.lower()):
        cfg = config(n)
        out.append({'name': n, 'title': mod.TITLE, 'about': getattr(mod, 'ABOUT', ''), 'on': n in configured(),
                    'keyHelp': getattr(mod, 'KEY_HELP', ''), 'keyPage': getattr(mod, 'KEY_PAGE', ''), 'mcp': hasattr(mod, 'MCP'),
                    'fields': [{'k': k, 'label': lab, 'secret': sec, 'value': '' if sec else cfg.get(k, ''), 'has': bool(cfg.get(k)),
                                'example': getattr(mod, 'EXAMPLE', '') if k.endswith('url') else ''} for k, lab, sec in mod.FIELDS]})
    return out


def mcp_for(task):
    """{server name: Claude Code MCP config} for the MCP integrations this request is about."""
    out = {}
    for n in configured():
        mod = modules()[n]
        if hasattr(mod, 'MCP') and re.search(mod.WORDS, task, re.I):
            out[n] = mod.MCP(config(n))
    return out


def mcp_risky(tool, args):
    """tool: 'mcp__<server>__<tool>' -> a question, or ''."""
    parts = tool.split('__')
    mod = modules().get(parts[1]) if len(parts) > 2 else None
    f = getattr(mod, 'mcp_risky', None)
    return f(parts[2], args or {}) if f else ''


def hints(task, cmd):
    """Lines for the request: only the integrations the words are about."""
    out = []
    for n in configured():
        mod = modules()[n]
        if re.search(mod.WORDS, task, re.I):
            out.append(f'- {mod.TITLE}: ' + mod.HELP.replace('{CMD}', f'{cmd} {n}'))
    return '\n'.join(out)


def risky(cmd_tail):
    """cmd_tail: '<name> <subcommand> …' -> a question, or ''."""
    parts = cmd_tail.split()
    mod = modules().get(parts[0]) if parts else None
    return (getattr(mod, 'RISKY', {}) or {}).get(parts[1].strip('"\''), '') if mod and len(parts) > 1 else ''


def run(name, args):
    mod = modules().get(name)
    if not mod:
        return f'no integration called {name}'
    cfg = config(name)
    if not cfg:
        return f'{mod.TITLE} is not set up: Atticus Settings → Homelab'
    try:
        return mod.run(cfg, args)
    except urllib.error.HTTPError as e:
        return f'{mod.TITLE} said {e.code} {e.reason}' + (' (check the API key in Settings → Homelab)' if e.code in (401, 403) else '')
    except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
        return f"can't reach {mod.TITLE} at {cfg.get('url', '?')}: {getattr(e, 'reason', e)}. Away from home? It may need Tailscale."


# ---- small helpers for the modules ----

def http(method, url, headers=None, body=None, timeout=10, **query):
    q = {k: v for k, v in query.items() if v is not None}
    full = url + ('?' + urllib.parse.urlencode(q, doseq=True) if q else '')
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(full, data=data, method=method,
                                 headers={'Accept': 'application/json', **({'Content-Type': 'application/json'} if data else {}), **(headers or {})})
    raw = urllib.request.urlopen(req, timeout=timeout).read()
    try:
        return json.loads(raw or b'null')
    except ValueError:
        return raw.decode('utf-8', 'replace')


def year(s):
    return f' ({str(s)[:4]})' if s else ''


def arg(args, i, default=''):
    return args[i] if len(args) > i else default


def opt(args, flag, default=''):
    """--flag value from args (removed from them)."""
    if flag in args:
        i = args.index(flag)
        v = args[i + 1] if i + 1 < len(args) else default
        del args[i:i + 2]
        return v
    return default
