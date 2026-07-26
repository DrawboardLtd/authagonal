import { useState, useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { CardTitle, CardFooter } from '../components/ui/card';
import { Button } from '../components/ui/button';
import { Alert } from '../components/ui/alert';

const SCOPE_LABELS: Record<string, string> = {
  openid: 'consent.scopeOpenid',
  profile: 'consent.scopeProfile',
  email: 'consent.scopeEmail',
  offline_access: 'consent.scopeOfflineAccess',
  address: 'consent.scopeAddress',
  phone: 'consent.scopePhone',
};

/**
 * Scopes the user is not offered a choice about. `openid` is what makes this an OpenID Connect
 * request at all — without it no id_token is issued and the client has nothing to sign the user in
 * with, so presenting it as optional would only produce a grant that cannot work.
 */
const REQUIRED_SCOPES = new Set(['openid']);

interface ConsentScopeInfo {
  name: string;
  displayName?: string | null;
  description?: string | null;
  emphasize?: boolean;
  required?: boolean;
  /** Present from server 0.16.3 onward. Scopes sharing one are shown together under it. */
  group?: string | null;
}

interface ConsentInfo {
  clientId: string;
  clientName: string;
  description?: string;
  clientUri?: string;
  logoUri?: string;
  scopes: string[];
  /** Present from server 0.16.1 onward; absent against older hosts. */
  scopeDetails?: ConsentScopeInfo[];
}

export default function ConsentPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const clientId = searchParams.get('client_id') ?? '';
  const scope = searchParams.get('scope') ?? 'openid';
  const returnUrl = searchParams.get('returnUrl') ?? '/';

  const [info, setInfo] = useState<ConsentInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [granted, setGranted] = useState<Set<string>>(new Set());
  const [openGroups, setOpenGroups] = useState<Set<string>>(new Set());

  // Prefer what the server says about each scope. Falling back to bare names covers an older host and
  // the case where /consent/info failed outright.
  const offered = useMemo<ConsentScopeInfo[]>(() => {
    if (info?.scopeDetails?.length) return info.scopeDetails;
    const names = info?.scopes ?? scope.split(' ').filter(Boolean);
    return names.map((name) => ({ name }));
  }, [info, scope]);

  useEffect(() => {
    fetch(`/consent/info?client_id=${encodeURIComponent(clientId)}&scope=${encodeURIComponent(scope)}`)
      .then(async (res) => {
        if (!res.ok) throw new Error('Failed to load');
        setInfo(await res.json());
      })
      .catch(() => setError(t('consent.loadError')))
      .finally(() => setLoading(false));
  }, [clientId, scope, t]);

  // Everything starts ticked: this screen is an opportunity to grant LESS than the app asked for, not
  // a form the user has to fill in before they can continue.
  useEffect(() => setGranted(new Set(offered.map((s) => s.name))), [offered]);

  /**
   * What to call a scope. The registered display name wins — it is the only wording anyone actually
   * chose. Standard OIDC scopes fall back to our own translations, because a product should not have
   * to register `openid` and `email` to get sensible text for them. Failing both, the raw name is
   * shown: unhelpful, but honest, and far better than a guess on a permission prompt.
   */
  function describeScope(s: ConsentScopeInfo): string {
    if (s.displayName) return s.displayName;
    const labelKey = SCOPE_LABELS[s.name];
    return labelKey ? t(labelKey) : s.name;
  }

  /** A scope is locked if the protocol needs it, or if it was registered as not declinable. */
  function isRequired(s: ConsentScopeInfo): boolean {
    return REQUIRED_SCOPES.has(s.name) || s.required === true;
  }

  /**
   * The scopes that stand alone, and the ones filed under a heading.
   *
   * A client asking for fifteen scopes produces a list nobody reads to the end of. Grouping shortens it
   * without hiding anything: the heading names what is inside even while collapsed, and the individual
   * boxes are one click away for anyone who wants to grant part of a group.
   *
   * Groups keep the order the server sent them in, so the screen reads the way whoever registered the
   * scopes intended rather than alphabetically.
   */
  const ungrouped = offered.filter((s) => !s.group);
  const groups = offered.reduce<[string, ConsentScopeInfo[]][]>((acc, s) => {
    if (!s.group) return acc;
    const existing = acc.find(([name]) => name === s.group);
    if (existing) existing[1].push(s);
    else acc.push([s.group, [s]]);
    return acc;
  }, []);

  function toggleGroupOpen(group: string) {
    setOpenGroups((current) => {
      const next = new Set(current);
      if (next.has(group)) next.delete(group);
      else next.add(group);
      return next;
    });
  }

  /** All on, or all off — whichever the group is not already entirely. Required scopes never move. */
  function toggleGroup(items: ConsentScopeInfo[]) {
    const optional = items.filter((i) => !isRequired(i));
    if (optional.length === 0) return;

    const turningOff = optional.every((i) => granted.has(i.name));
    setGranted((current) => {
      const next = new Set(current);
      for (const i of optional) {
        if (turningOff) next.delete(i.name);
        else next.add(i.name);
      }
      return next;
    });
  }

  function renderScope(s: ConsentScopeInfo) {
    const required = isRequired(s);
    return (
      <label
        key={s.name}
        className={`flex items-start gap-3 p-3 bg-gray-50 dark:bg-gray-800/60 rounded-lg ${
          required ? 'cursor-default' : 'cursor-pointer'
        }`}
      >
        <input
          type="checkbox"
          checked={granted.has(s.name)}
          disabled={required || submitting}
          onChange={() => toggle(s)}
          className="h-4 w-4 shrink-0 mt-0.5 accent-primary"
        />
        <span className="flex-1 min-w-0">
          <span
            className={`block text-sm ${
              s.emphasize
                ? 'font-medium text-gray-900 dark:text-gray-100'
                : 'text-gray-700 dark:text-gray-300'
            }`}
          >
            {describeScope(s)}
          </span>
          {s.description && (
            <span className="block text-xs text-gray-500 dark:text-gray-400 mt-0.5">
              {s.description}
            </span>
          )}
        </span>
        {required && (
          <span className="text-xs text-gray-500 dark:text-gray-400 shrink-0 mt-0.5">
            {t('consent.required')}
          </span>
        )}
      </label>
    );
  }

  function toggle(s: ConsentScopeInfo) {
    if (isRequired(s)) return;
    setGranted((current) => {
      const next = new Set(current);
      if (next.has(s.name)) {
        next.delete(s.name);
      } else {
        next.add(s.name);
      }
      return next;
    });
  }

  async function handleDecision(decision: 'allow' | 'deny') {
    setSubmitting(true);
    setError('');
    try {
      const res = await fetch('/consent', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          clientId,
          decision,
          // Only what the user left ticked. The server records this as the grant and the scopes it was
          // chosen from separately, so a scope declined here is not re-prompted on every sign-in.
          scopes: [...granted],
          returnUrl,
        }),
      });
      const data = await res.json();
      if (data.redirect) {
        window.location.href = data.redirect;
      }
    } catch {
      setError(t('consent.submitError'));
      setSubmitting(false);
    }
  }

  if (loading) {
    return (
      <p className="text-sm text-gray-500 dark:text-gray-400 text-center">{t('consent.loading')}</p>
    );
  }

  return (
    <>
      {info?.logoUri && (
        <div className="flex justify-center mb-4">
          <img
            src={info.logoUri}
            alt={info.clientName}
            className="h-12 w-12 rounded-lg object-contain"
            onError={(e) => {
              (e.currentTarget as HTMLImageElement).style.display = 'none';
            }}
          />
        </div>
      )}
      <CardTitle>{t('consent.title', { appName: info?.clientName ?? clientId })}</CardTitle>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">{t('consent.subtitle', { appName: info?.clientName ?? clientId })}</p>

      {info?.description && (
        <p className="text-sm text-gray-600 dark:text-gray-300 mb-4">{info.description}</p>
      )}
      {info?.clientUri && (
        <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
          <a
            href={info.clientUri}
            target="_blank"
            rel="noopener noreferrer"
            className="text-primary hover:underline"
          >
            {info.clientUri}
          </a>
        </p>
      )}

      {error && <Alert variant="error">{error}</Alert>}

      <p className="text-xs text-gray-500 dark:text-gray-400 mb-3">{t('consent.selectHint')}</p>

      <div className="space-y-2 mb-6">
        {ungrouped.map(renderScope)}

        {groups.map(([group, items]) => {
          const on = items.filter((i) => granted.has(i.name)).length;
          const open = openGroups.has(group);
          const lockedGroup = items.every(isRequired);

          return (
            <div key={group} className="bg-gray-50 dark:bg-gray-800/60 rounded-lg">
              <div className="flex items-start gap-3 p-3">
                <input
                  type="checkbox"
                  checked={on > 0}
                  ref={(el) => {
                    // Some-but-not-all reads as neither ticked nor blank, which is the honest answer
                    // when a group holds a mix and is the only state a single box can express.
                    if (el) el.indeterminate = on > 0 && on < items.length;
                  }}
                  disabled={lockedGroup || submitting}
                  onChange={() => toggleGroup(items)}
                  className="h-4 w-4 shrink-0 mt-0.5 accent-primary"
                />
                <button
                  type="button"
                  onClick={() => toggleGroupOpen(group)}
                  aria-expanded={open}
                  className="flex-1 min-w-0 text-left"
                >
                  <span className="block text-sm font-medium text-gray-900 dark:text-gray-100">
                    {group}
                  </span>
                  {/* Named even while collapsed. Hiding what is inside behind a chevron would make the
                      screen shorter by making the decision less informed, which is the wrong trade on a
                      consent screen. */}
                  <span className="block text-xs text-gray-500 dark:text-gray-400 mt-0.5">
                    {items.map(describeScope).join(' · ')}
                  </span>
                </button>
                <span className="text-xs text-gray-500 dark:text-gray-400 shrink-0 mt-0.5 tabular-nums">
                  {on}/{items.length}
                </span>
              </div>

              {open && <div className="px-3 pb-3 pl-10 space-y-2">{items.map(renderScope)}</div>}
            </div>
          );
        })}
      </div>

      <div className="flex gap-3">
        <Button onClick={() => handleDecision('allow')} loading={submitting} className="flex-1">
          {t('consent.allow')}
        </Button>
        <Button variant="secondary" onClick={() => handleDecision('deny')} disabled={submitting} className="flex-1">
          {t('consent.deny')}
        </Button>
      </div>

      <CardFooter>
        <p className="text-xs text-gray-600 dark:text-gray-300">{t('consent.hint')}</p>
      </CardFooter>
    </>
  );
}
