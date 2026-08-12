# Baileys emergency patch policy

An emergency patch is a small, reviewable compatibility repair applied only
when the stable connector is otherwise unable to preserve an existing user
workflow safely.

Allowed scope:

- a narrow adapter around a changed field/event shape;
- a bounded retry/backoff, target-verification, history, label, LID, media, or
  session-compatibility fix;
- a feature-specific safe-mode trigger that prevents duplicate/wrong-target
  automatic sends while retaining reads and local CRM access.

Disallowed scope:

- copying or maintaining a broad Baileys fork;
- remote code, runtime package download, silent connector swapping, Meta Cloud
  API migration, session format replacement, or QR flow replacement;
- removal/renaming of commands, events, database fields, credential targets, or
  user-visible Inbox behavior;
- logging or exporting authentication/session/customer/message material.

Every emergency patch requires a named incident/error signature, a focused
fixture or replay that fails before and passes after, the complete compatibility
and upgrade gates, source/license notice updates when applicable, and a release
note describing impact and rollback. If the complete gates cannot pass, the
patch is not released; safe mode remains the containment mechanism.
