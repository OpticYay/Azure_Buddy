import { animate, query, stagger, state, style, transition, trigger } from '@angular/animations';

// Motion here is deliberately sparse. The design's boldness is spent on the log format and the
// brass work item stamp; animation's whole job is to make state changes legible, not to decorate.

/** Log entries fade up as they're written into the thread. Small distance, quick - a new entry
 * should register without pulling the eye away from what you were reading. */
export const fadeSlideIn = trigger('fadeSlideIn', [
  transition(':enter', [
    style({ opacity: 0, transform: 'translateY(6px)' }),
    animate(
      '200ms cubic-bezier(0.3, 0, 0.2, 1)',
      style({ opacity: 1, transform: 'translateY(0)' }),
    ),
  ]),
]);

/** Scale + fade, for things that appear in place: the screenshot preview once attached, the
 * connection-check result icon once the request lands. */
export const popIn = trigger('popIn', [
  transition(':enter', [
    style({ opacity: 0, transform: 'scale(0.88)' }),
    animate('170ms cubic-bezier(0.3, 0, 0.2, 1)', style({ opacity: 1, transform: 'scale(1)' })),
  ]),
]);

/** Cross-fade on session switch. MessageThread's component instance is REUSED across switches (only
 * its `sessionId` input changes - see message-thread.ts), so there is no DOM enter/leave to hook
 * `:enter` onto. Instead this is bound as `[@crossFade]="sessionId()"`: `'* => *'` matches any
 * change in the bound value, so it replays every time the id changes, including between two real
 * sessions. */
export const crossFade = trigger('crossFade', [
  transition('* => *', [style({ opacity: 0 }), animate('170ms ease-out', style({ opacity: 1 }))]),
]);

/** Toasts slide in from the right edge and fade out on the way out. The `:leave` transition works
 * even though the element is about to be destroyed - Angular holds the DOM removal until it ends. */
export const toastSlide = trigger('toastSlide', [
  transition(':enter', [
    style({ opacity: 0, transform: 'translateX(20px)' }),
    animate(
      '190ms cubic-bezier(0.3, 0, 0.2, 1)',
      style({ opacity: 1, transform: 'translateX(0)' }),
    ),
  ]),
  transition(':leave', [
    animate('170ms ease-in', style({ opacity: 0, transform: 'translateX(20px)' })),
  ]),
]);

/** Conversation rows arriving in the sidebar, one shortly after the next.
 *
 * `query(...)` reaches INTO this element's children to animate them (a trigger normally only
 * animates the element it's attached to), and `stagger(...)` offsets each match's start by a fixed
 * step so they cascade instead of all moving at once. `{ optional: true }` stops Angular throwing
 * when the list is empty. 35ms is deliberately short - it should read as the list settling into
 * place, not as a sequence you're made to wait through. */
export const listStagger = trigger('listStagger', [
  transition(':enter', [
    query(
      '.session',
      [
        style({ opacity: 0, transform: 'translateX(-8px)' }),
        stagger(
          35,
          animate('200ms cubic-bezier(0.3, 0, 0.2, 1)', style({ opacity: 1, transform: 'none' })),
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
        style({ opacity: 0, transform: 'translateY(8px)' }),
        stagger(
          55,
          animate('260ms cubic-bezier(0.3, 0, 0.2, 1)', style({ opacity: 1, transform: 'none' })),
        ),
      ],
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
  transition('expanded <=> collapsed', animate('220ms cubic-bezier(0.3, 0, 0.2, 1)')),
]);
