import test from 'node:test';
import assert from 'node:assert/strict';
import { createAuthorizationTracker, authorizationState, authorizationPollDelay } from './windows-authorization.mjs';

test('fresh GUI recovery closes authorization without waiting for network readiness', () => {
  assert.deepEqual(authorizationState({guest_system:'windows-11', guest_attention:'requires_system_attention', ready:false}), {pending:true, resolved:false});
  assert.deepEqual(authorizationState({guest_system:'windows-11', guest_gui_ready:true, ready:false}), {pending:false, resolved:true});
  assert.deepEqual(authorizationState({guest_system:'windows-11', ready:false}), {pending:false, resolved:false});
  assert.deepEqual(authorizationState({guest_system:'windows-remote', guest_gui_ready:true}), {pending:false, resolved:false});
});
test('authorization stays responsive even while its owner is in background', () => {
  assert.equal(authorizationPollDelay({windows:true,pending:true,visible:false}), 500);
  assert.equal(authorizationPollDelay({windows:true,visible:true}), 1000);
  assert.equal(authorizationPollDelay({windows:false,visible:false}), 15000);
});

test('opens once per attention episode, permitting manual dismissal', () => {
  const tracker = createAuthorizationTracker();
  const pending = {eligible:true,pending:true};
  assert.equal(tracker.update(pending), 'open');
  assert.equal(tracker.update(pending), null);
  assert.equal(tracker.update({eligible:true}), null, 'unknown status must not close the prompt');
  assert.equal(tracker.update({eligible:true,resolved:true}), 'close');
  assert.equal(tracker.update(pending), 'open');
});
test('does not open remotely and closes when changing away from local context', () => {
  const tracker = createAuthorizationTracker();
  assert.equal(tracker.update({eligible:false,pending:true}), null);
  assert.equal(tracker.update({eligible:true,pending:true}), 'open');
  assert.equal(tracker.update({eligible:false}), 'close');
  assert.equal(tracker.update({eligible:true,pending:true}), 'open');
});
test('confirmed stop closes and resets attention without starting the VM', () => {
  const tracker = createAuthorizationTracker();
  tracker.update({eligible:true,pending:true});
  assert.equal(tracker.update({eligible:true,stopped:true}), 'close');
  assert.equal(tracker.update({eligible:true,stopped:true}), null);
  assert.equal(tracker.update({eligible:true,pending:true}), 'open');
});

test('application attention overrides ready flags', () => {
  for (const guest_attention of ['windows_application_prompt']) {
    assert.deepEqual(authorizationState({guest_system:'windows-11', guest_attention, ready:true, guest_gui_ready:true}), {pending:true,resolved:false});
  }
});

test('login does not automatically open a console or claim GUI recovery', () => {
  const state = authorizationState({guest_system:'windows-11', guest_attention:'requires_gui_login', ready:true, guest_gui_ready:true});
  assert.deepEqual(state, {pending:false, resolved:false});
  assert.equal(createAuthorizationTracker().update({eligible:true,...state}), null);
});
test('manual console stays open on already-ready status and follows a later attention episode', () => {
  const tracker = createAuthorizationTracker();
  assert.equal(tracker.update({eligible:true,manual:true,resolved:true}), 'open');
  assert.equal(tracker.update({eligible:true,resolved:true}), null);
  assert.equal(tracker.update({eligible:true}), null);
  assert.equal(tracker.update({eligible:true,pending:true}), null);
  assert.equal(tracker.update({eligible:true}), null);
  assert.equal(tracker.update({eligible:true,resolved:true}), 'close');
});
test('manual console closes on confirmed stop and context change', () => {
  for (const end of [{eligible:false}, {eligible:true,stopped:true}]) {
    const tracker = createAuthorizationTracker();
    assert.equal(tracker.update({eligible:true,manual:true}), 'open');
    assert.equal(tracker.update(end), 'close');
  }
});

test('consecutive application, security and login prompts share one episode through unknown status', () => {
  const tracker = createAuthorizationTracker();
  const update = status => tracker.update({eligible:true, ...authorizationState({guest_system:'windows-11',...status})});
  assert.equal(update({guest_attention:'windows_application_prompt'}), 'open');
  assert.equal(update({}), null);
  assert.equal(update({guest_attention:'windows_security_prompt',guest_gui_ready:true}), null);
  assert.equal(update({guest_attention:'requires_gui_login',ready:true}), null);
  assert.equal(update({guest_gui_ready:true}), 'close');
  assert.equal(update({guest_attention:'windows_application_prompt'}), 'open');
});
