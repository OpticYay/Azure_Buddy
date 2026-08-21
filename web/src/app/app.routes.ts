import { Routes } from '@angular/router';

import { authGuard } from './core/guards/auth-guard';
import { redirectIfAuthenticatedGuard } from './core/guards/redirect-if-authenticated-guard';
import { adminGuard } from './core/guards/admin-guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login/login').then((m) => m.Login),
    canActivate: [redirectIfAuthenticatedGuard],
  },
  {
    path: 'register',
    loadComponent: () => import('./features/auth/register/register').then((m) => m.Register),
    canActivate: [redirectIfAuthenticatedGuard],
  },
  {
    path: 'forgot-password',
    loadComponent: () =>
      import('./features/auth/forgot-password/forgot-password').then((m) => m.ForgotPassword),
    canActivate: [redirectIfAuthenticatedGuard],
  },
  {
    // No redirectIfAuthenticatedGuard here (unlike login/register/forgot-password): a reset link can
    // land on this page from a device/tab that's still logged in (e.g. the same browser the user
    // requested the reset from) - the reset itself doesn't require being logged out, so it shouldn't
    // be blocked either way.
    path: 'reset-password',
    loadComponent: () =>
      import('./features/auth/reset-password/reset-password').then((m) => m.ResetPassword),
  },
  {
    // Same reasoning as reset-password - a user who registered and is already logged in should still
    // be able to open their confirmation link and have it work.
    path: 'confirm-email',
    loadComponent: () =>
      import('./features/auth/confirm-email/confirm-email').then((m) => m.ConfirmEmail),
  },
  {
    // A route with no `component`, only `children`, is a "layout route": AppShell renders a shared
    // frame (top nav, logout button) with its own <router-outlet> inside, and whichever child route
    // matched (chat, or settings/ado) renders inside THAT inner outlet. This is how "every protected
    // page shares the same header" is expressed in the route table rather than copy-pasted into each
    // page component. `canActivate` on the PARENT route protects every child route beneath it at once.
    path: '',
    loadComponent: () => import('./layout/app-shell/app-shell').then((m) => m.AppShell),
    canActivate: [authGuard],
    children: [
      {
        path: 'chat',
        loadComponent: () => import('./features/chat/chat-page/chat-page').then((m) => m.ChatPage),
      },
      {
        // Same component as above; ChatPage reads the :sessionId param itself to know whether it's
        // showing a specific session or the "nothing selected yet" empty state.
        path: 'chat/:sessionId',
        loadComponent: () => import('./features/chat/chat-page/chat-page').then((m) => m.ChatPage),
      },
      {
        path: 'settings/ado',
        loadComponent: () =>
          import('./features/settings/ado-settings/ado-settings').then((m) => m.AdoSettings),
      },
      {
        path: 'profile',
        loadComponent: () => import('./features/profile/profile').then((m) => m.Profile),
      },
      {
        // Both guards run: authGuard already applies to this whole layout route, and adminGuard adds
        // the further "and are they an admin" check on top - see admin-guard.ts for why a non-admin
        // is bounced to /chat rather than /login here.
        path: 'admin/llm',
        loadComponent: () =>
          import('./features/admin/llm-settings/llm-settings').then((m) => m.LlmSettings),
        canActivate: [adminGuard],
      },
      {
        path: 'admin/work-item-states',
        loadComponent: () =>
          import('./features/admin/work-item-states/work-item-states').then(
            (m) => m.WorkItemStates,
          ),
        canActivate: [adminGuard],
      },
      { path: '', pathMatch: 'full', redirectTo: 'chat' },
    ],
  },
  { path: '**', redirectTo: 'chat' },
];
