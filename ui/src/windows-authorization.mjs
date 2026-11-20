// Dismissal is remembered for this attention episode, not permanently.
export function authorizationState(status) {
  if (status?.guest_system !== 'windows-11' || status.guest_attention === 'requires_gui_login') return {pending:false, resolved:false};
  const pending = ['requires_system_attention', 'windows_security_prompt', 'windows_application_prompt'].includes(status.guest_attention);
  return {pending, resolved:!pending && (status.ready === true || status.guest_gui_ready === true)};
}
export function authorizationPollDelay({windows, pending, visible, jobRunning}) {
  if (pending) return 500;
  if (windows && visible) return 1000;
  return jobRunning ? 1200 : visible ? 3000 : 15000;
}
export function authorizationCopy(reason) {
  if (reason === 'requires_gui_login') return {title:'Windows 需要登录或解锁', body:'请在客机画面中自行登录或解锁。完成后自动返回 Cage。'};
  if (reason === 'windows_application_prompt') return {title:'Windows 应用正在等待你处理', body:'请在客机画面中查看并处理应用提示。完成后自动返回 Cage。'};
  if (['requires_system_attention', 'windows_security_prompt'].includes(reason)) return {title:'Windows 系统正在等待你处理', body:'请在客机画面中查看系统提示，并自行选择或输入所需信息。'};
  return {title:'Windows 系统／应用弹窗', body:'请在客机画面中处理提示，完成后点击“返回 Cage”。'};
}
export function createAuthorizationTracker() {
  let active = false, observedPending = false;
  return { update({eligible, pending = false, resolved = false, stopped = false, manual = false}) {
    if (!eligible || stopped) {
      const close = active;
      active = observedPending = false;
      return close ? 'close' : null;
    }
    if (manual) {
      active = true;
      observedPending = observedPending || pending;
      return 'open';
    }
    if (pending) observedPending = true;
    if (resolved && observedPending) {
      const close = active;
      active = observedPending = false;
      return close ? 'close' : null;
    }
    if (pending && !active) { active = true; return 'open'; }
    return null;
  }};
}
