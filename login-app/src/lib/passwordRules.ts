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
