import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { firstValueFrom } from 'rxjs';

import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth-interceptor';
import { AuthService } from './core/services/auth.service';

// ── What is "dependency injection" (DI), and why does this file matter? ────────────────────────────
// Later, we'll write services like `AuthService` and have components simply declare "I need an
// AuthService" in their constructor, without ever writing `new AuthService()` themselves. Angular
// creates that instance FOR them and hands it over - that's dependency injection: instead of a class
// constructing the things it depends on, those things are "injected" into it from outside. The benefit
// is that a component doesn't need to know HOW to build an AuthService (what it needs, what it talks
// to) - it just asks for one, and Angular's DI system supplies it. This also makes testing easier
// later: a test can inject a fake AuthService instead of the real one, without touching the component.
//
// For DI to know how to build things like HttpClient (used by every service that talks to the API),
// something has to register it once, globally, at app startup. That's what `providers` here does -
// it's the app's root list of "here's how to construct these injectable things." Every component and
// service in the whole app can then ask for an HttpClient and get the one configured here.
//
// `withInterceptors([authInterceptor])` plugs our own function into HttpClient's request pipeline -
// every single HTTP request/response flows through it. See core/interceptors/auth-interceptor.ts
// (built in the Auth piece) for what it actually does: attaching the JWT and handling token refresh.
// ── What is @angular/animations, and how is it different from a plain CSS transition? ──────────────
// A CSS `transition` (used elsewhere in this app - e.g. styles.css's button hover/press effect) only
// animates a property changing value WHILE the element stays in the DOM - it can't animate an element
// actually being added or removed, because by the time Angular removes it, the browser has nothing
// left to transition. Angular's animation system plugs into the framework's own lifecycle: it can
// detect "this element is about to be inserted" or "this element is about to be removed" and run an
// animation THEN, delaying the actual removal until the animation finishes. It also lets you define
// named "states" (e.g. a sidebar being 'collapsed' vs 'expanded') and a transition between them as a
// reusable, declarative unit (see shared/animations.ts) instead of manually toggling CSS classes and
// hoping the timing lines up. `provideAnimationsAsync()` (rather than the older `provideAnimations()`)
// loads the animation engine's code lazily, in a separate chunk, only once something on the page
// actually needs it - keeping the initial bundle smaller for a page that used no animations at all.
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideAnimationsAsync(),
    // provideAppInitializer registers a function that runs once, at app startup, BEFORE the app
    // finishes bootstrapping and the router starts rendering routes/running guards - so authGuard's
    // very first check of auth.isAuthenticated() already reflects a restored session, instead of
    // momentarily seeing "logged out" and bouncing to /login before the refresh call even finishes.
    // `firstValueFrom` converts the Observable tryRestoreSession() returns into a Promise, since
    // that's what Angular's bootstrap process waits on here.
    provideAppInitializer(() => firstValueFrom(inject(AuthService).tryRestoreSession())),
  ],
};
