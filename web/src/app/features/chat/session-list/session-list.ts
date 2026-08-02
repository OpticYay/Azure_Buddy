import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';

import { ChatService } from '../../../core/services/chat.service';
import { ChatSessionSummary } from '../../../core/models/chat.models';
import { listStagger, sidebarWidth } from '../../../shared/animations';

const PAGE_SIZE = 20;
const COLLAPSED_STORAGE_KEY = 'azurebuddy_sidebar_collapsed';
/** Below this width a full-width rail would leave the conversation itself unusably narrow, so the
 * rail is forced to its icon form regardless of the stored preference. Kept in sync with nothing
 * else in CSS on purpose - see the `collapsed` computed below for why this lives in TypeScript. */
const NARROW_SCREEN = '(max-width: 720px)';

// `DatePipe` is what powers the `| date: 'short'` in the template - a "pipe" is a small, reusable
// transform you apply to a value right in the template with `|` (piping the value through it), instead
// of writing `formatDate(session.updatedAt)` in the component class every place you display a date.
// Like everything else here it must be explicitly imported since components are standalone.
@Component({
  selector: 'app-session-list',
  imports: [RouterLink, RouterLinkActive, DatePipe],
  templateUrl: './session-list.html',
  styleUrl: './session-list.css',
  animations: [sidebarWidth, listStagger],
})
export class SessionList implements OnInit {
  private readonly chatService = inject(ChatService);
  private readonly router = inject(Router);

  readonly sessions = signal<ChatSessionSummary[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly creatingNew = signal(false);

  // ── Collapsed state: one source of truth ──────────────────────────────────────────────────────
  // Read synchronously at construction so the rail renders in its remembered state on the very
  // first frame, rather than flashing expanded and then snapping shut.
  private readonly preferCollapsed = signal(localStorage.getItem(COLLAPSED_STORAGE_KEY) === 'true');

  /** True while the viewport is too narrow for a full rail. Tracked here in TypeScript rather than
   * as a CSS media query on purpose: the rail's width is applied by @angular/animations as an
   * INLINE style, which a stylesheet rule can't beat without `!important`, and duplicating every
   * collapsed rule inside a breakpoint is how those two copies drift apart. Deriving `collapsed`
   * from both inputs means the CSS has exactly one collapsed state to describe. */
  private readonly narrowScreen = signal(window.matchMedia(NARROW_SCREEN).matches);

  readonly collapsed = computed(() => this.narrowScreen() || this.preferCollapsed());

  /** The toggle is hidden when the screen forces the issue - offering a control that visibly does
   * nothing is worse than not offering it. */
  readonly canToggle = computed(() => !this.narrowScreen());

  constructor() {
    const query = window.matchMedia(NARROW_SCREEN);
    const onChange = (event: MediaQueryListEvent) => this.narrowScreen.set(event.matches);
    query.addEventListener('change', onChange);
    // DestroyRef is Angular's hook for "run this when the component is torn down" - without removing
    // the listener, every visit to this route would leave another one attached to matchMedia.
    inject(DestroyRef).onDestroy(() => query.removeEventListener('change', onChange));
  }

  // Pagination state: how many pages we've loaded so far, and whether the server has more beyond
  // that. hasMore now comes straight from the API response (PagedResult.HasMore) instead of being
  // recomputed here from totalCount/items.length every time - one less place that arithmetic could
  // get out of sync with what the server actually knows.
  private currentPage = 0;
  readonly hasMore = signal(false);
  readonly loadingMore = signal(false);

  ngOnInit(): void {
    this.loadNextPage(true);
  }

  toggleCollapsed(): void {
    const next = !this.preferCollapsed();
    this.preferCollapsed.set(next);
    localStorage.setItem(COLLAPSED_STORAGE_KEY, String(next));
  }

  /** First letter of the title, for the icon-only rail's per-session avatar when collapsed - there's
   * no room for a full title, but a single initial still gives a (weak) visual "which one is this"
   * hint, and every row at least looks distinct from the others instead of being identical icons. */
  initialOf(title: string): string {
    return title.trim().charAt(0).toUpperCase() || '?';
  }

  private loadNextPage(isFirstLoad: boolean): void {
    const nextPage = this.currentPage + 1;
    (isFirstLoad ? this.loading : this.loadingMore).set(true);
    this.loadError.set(null);

    this.chatService.listSessions(nextPage, PAGE_SIZE).subscribe({
      next: (result) => {
        this.currentPage = result.page;
        // Append rather than replace, so "Load more" grows the list instead of resetting scroll position.
        this.sessions.update((existing) => [...existing, ...result.items]);
        this.hasMore.set(result.hasMore);
        this.loading.set(false);
        this.loadingMore.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadingMore.set(false);
        this.loadError.set('Could not load your chat sessions.');
      },
    });
  }

  loadMore(): void {
    this.loadNextPage(false);
  }

  newChat(): void {
    this.creatingNew.set(true);
    this.chatService.createSession(null).subscribe({
      next: (session) => {
        this.creatingNew.set(false);
        // Add the new session to the top of the list immediately, so the sidebar reflects it without
        // waiting for a full reload - a small but common pattern: update local state optimistically
        // from a response you already have, instead of re-fetching the whole list just to see one change.
        this.sessions.update((existing) => [
          { id: session.id, title: session.title, createdAt: session.createdAt, updatedAt: session.updatedAt },
          ...existing,
        ]);
        this.router.navigate(['/chat', session.id]);
      },
      error: () => {
        this.creatingNew.set(false);
        this.loadError.set('Could not start a new chat.');
      },
    });
  }

  deleteSession(session: ChatSessionSummary, event: Event): void {
    // This button sits inside the same row as a routerLink <a> - stop the click from also triggering
    // that link's navigation (otherwise deleting a session would also navigate to it first).
    event.stopPropagation();
    event.preventDefault();

    if (!confirm(`Delete "${session.title}"? This cannot be undone.`)) {
      return;
    }

    const wasCurrentlyOpen = this.router.url.includes(session.id);

    this.chatService.deleteSession(session.id).subscribe({
      next: () => {
        this.sessions.update((existing) => existing.filter((s) => s.id !== session.id));
        if (wasCurrentlyOpen) {
          this.router.navigateByUrl('/chat');
        }
      },
      error: () => this.loadError.set('Could not delete that session.'),
    });
  }
}
