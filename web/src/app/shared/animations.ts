import { animate, query, stagger, state, style, transition, trigger } from '@angular/animations';

// The redesign asked for motion with real presence, not the previous pass's "just enough to
// register" restraint - so every curve below is the firmer, more expressive one from tokens.css
// (--t-spring's cubic-bezier(0.16, 1, 0.3, 1), an expo-out shape with a felt snap at the end) and
// every distance is bigger than it used to be. The log format and the stamp motif still carry the
// design's identity; motion is now allowed to carry some of it too.

/** Log entries rise into the thread with a longer throw and a blur-clear, so a new entry visibly
 * arrives rather than just fading up half an inch. */
export const fadeSlideIn = trigger('fadeSlideIn', [
  transition(':enter', [
    style({ opacity: 0, transform: 'translateY(18px)', filter: 'blur(4px)' }),
    animate(
      '560ms cubic-bezier(0.16, 1, 0.3, 1)',
      style({ opacity: 1, transform: 'translateY(0)', filter: 'blur(0)' }),
    ),
  ]),
]);

/** Scale + fade, for things that appear in place: the screenshot preview once attached, the
 * connection-check result icon once the request lands. A small overshoot past 1 on the way in
 * gives it the spring the rest of the app now has. */
export const popIn = trigger('popIn', [
  transition(':enter', [
    style({ opacity: 0, transform: 'scale(0.8)' }),
    animate('420ms cubic-bezier(0.16, 1, 0.3, 1)', style({ opacity: 1, transform: 'scale(1)' })),
  ]),
]);

/** Cross-fade on session switch. MessageThread's component instance is REUSED across switches (only
 * its `sessionId` input changes - see message-thread.ts), so there is no DOM enter/leave to hook
 * `:enter` onto. Instead this is bound as `[@crossFade]="sessionId()"`: `'* => *'` matches any
 * change in the bound value, so it replays every time the id changes, including between two real
 * sessions. */
export const crossFade = trigger('crossFade', [
  transition('* => *', [
    style({ opacity: 0, transform: 'translateY(10px)' }),
    animate(
      '320ms cubic-bezier(0.16, 1, 0.3, 1)',
      style({ opacity: 1, transform: 'translateY(0)' }),
    ),
  ]),
]);

/** Toasts slide in from the right edge and fade out on the way out. The `:leave` transition works
 * even though the element is about to be destroyed - Angular holds the DOM removal until it ends. */
export const toastSlide = trigger('toastSlide', [
  transition(':enter', [
    style({ opacity: 0, transform: 'translateX(36px) scale(0.95)' }),
    animate(
      '420ms cubic-bezier(0.16, 1, 0.3, 1)',
      style({ opacity: 1, transform: 'translateX(0) scale(1)' }),
    ),
  ]),
  transition(':leave', [
    animate('220ms ease-in', style({ opacity: 0, transform: 'translateX(24px)' })),
  ]),
]);

/** Conversation rows arriving in the sidebar, one shortly after the next.
 *
 * `query(...)` reaches INTO this element's children to animate them (a trigger normally only
 * animates the element it's attached to), and `stagger(...)` offsets each match's start by a fixed
 * step so they cascade instead of all moving at once. `{ optional: true }` stops Angular throwing
 * when the list is empty. */
export const listStagger = trigger('listStagger', [
  transition(':enter', [
    query(
      '.session',
      [
        style({ opacity: 0, transform: 'translateX(-16px)' }),
        stagger(
          45,
          animate('440ms cubic-bezier(0.16, 1, 0.3, 1)', style({ opacity: 1, transform: 'none' })),
        ),
      ],
      { optional: true },
    ),
  ]),
]);

/** The empty-state composition: heading, then hint, then each starter chip. Same query/stagger
 * device as the sidebar, so an empty conversation assembles itself rather than appearing all at
 * once in the middle of a large blank area. */
export const openerStagger = trigger('openerStagger', [
  transition(':enter', [
    query(
      '.opener__lead, .opener__hint, .starter',
      [
        style({ opacity: 0, transform: 'translateY(16px)' }),
        stagger(
          80,
          animate('620ms cubic-bezier(0.16, 1, 0.3, 1)', style({ opacity: 1, transform: 'none' })),
        ),
      ],
      { optional: true },
    ),
  ]),
]);

/** Route swaps inside the shell (Chat / Connection / Model / States / your account). Bound as
 * `[@viewTransition]="prepareRoute(outlet)"` on the `.stage` wrapper around `<router-outlet>`
 * (see app-shell.ts/.html) - the bound value is a key derived from the activated route, so `'* <=>
 * *'` replays this on every real navigation. Only `:enter` is queried: the outgoing page is torn
 * down synchronously by the router, so there's no overlap window to choreograph a `:leave` against,
 * and querying one avoids the absolute-positioning dance that would otherwise be needed to keep two
 * routed pages in the flex layout at once. */
export const viewTransition = trigger('viewTransition', [
  transition('* <=> *', [
    query(':enter', [style({ opacity: 0, transform: 'translateY(14px)' })], { optional: true }),
    query(
      ':enter',
      animate(
        '420ms cubic-bezier(0.16, 1, 0.3, 1)',
        style({ opacity: 1, transform: 'translateY(0)' }),
      ),
      { optional: true },
    ),
  ]),
]);

/** Sidebar collapse/expand. Pixel values rather than var(--sidebar-width) because Angular's
 * animation engine interpolates between concrete values; these mirror the tokens in tokens.css, so
 * change both together. */
export const sidebarWidth = trigger('sidebarWidth', [
  state('expanded', style({ width: '264px' })),
  state('collapsed', style({ width: '60px' })),
  transition('expanded <=> collapsed', animate('380ms cubic-bezier(0.16, 1, 0.3, 1)')),
]);
