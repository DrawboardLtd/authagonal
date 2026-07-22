import type { TFunction } from 'i18next';
import type { PasswordPolicyRule } from '../types';

// The password-policy endpoint returns English labels; the rule ids are the
// localization contract. Unknown rule ids fall back to the server's label.
export function localizePasswordRuleLabel(t: TFunction, rule: PasswordPolicyRule): string {
  switch (rule.rule) {
    case 'minLength': return t('ruleMinLength', { count: rule.value ?? 8 });
    case 'uppercase': return t('ruleUppercase');
    case 'lowercase': return t('ruleLowercase');
    case 'digit': return t('ruleDigit');
    case 'specialChar': return t('ruleSpecialChar');
    default: return rule.label;
  }
}

export function localizePasswordRules(t: TFunction, rules: PasswordPolicyRule[]): PasswordPolicyRule[] {
  return rules.map((r) => ({ ...r, label: localizePasswordRuleLabel(t, r) }));
}

export interface PasswordRequirement {
  rule: string;
  label: string;
  met: boolean;
}

// Client-side mirror of the server's password policy, for live checklist feedback while typing.
// Unknown rule ids count as met — the server remains the enforcement gate.
export function evaluatePasswordRules(password: string, rules: PasswordPolicyRule[]): PasswordRequirement[] {
  return rules.map((r) => {
    let met = false;
    switch (r.rule) {
      case 'minLength': met = password.length >= (r.value ?? 8); break;
      case 'uppercase': met = /[A-Z]/.test(password); break;
      case 'lowercase': met = /[a-z]/.test(password); break;
      case 'digit': met = /[0-9]/.test(password); break;
      case 'specialChar': met = /[^A-Za-z0-9]/.test(password); break;
      default: met = true;
    }
    return { rule: r.rule, label: r.label, met };
  });
}
