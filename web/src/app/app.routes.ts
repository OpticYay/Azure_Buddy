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
    // canActivate on this parent route protects every child route beneath it at once.
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
        // Both guards run: authGuard already applies to the whole layout route; adminGuard adds the
        // further admin check. See admin-guard.ts for why a non-admin is bounced to /chat, not /login.
        path: 'admin/llm',
        loadComponent: () =>
          import('./features/admin/llm-settings/llm-settings').then((m) => m.LlmSettings),
        canActivate: [adminGuard],
      },
      { path: '', pathMatch: 'full', redirectTo: 'chat' },
    ],
  },
  { path: '**', redirectTo: 'chat' },
];
