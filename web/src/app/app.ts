import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

// ── What is an Angular "component"? ─────────────────────────────────────────────────────────────
// A component is the basic building block of an Angular UI: one piece of screen, bundled together
// with the TypeScript class that controls its behavior and the HTML template that describes what it
// looks like. Every visible thing in this app - the login form, one row in the session list, a single
// chat bubble - will be its own component. Apps are built by composing many small components together,
// the same way a page is built out of nested HTML elements, except each of our "elements" also carries
// its own logic and its own scoped CSS.
//
// The `@Component(...)` block above the class is a "decorator" - it attaches metadata to the plain
// TypeScript class below it, telling Angular three things:
//   - selector: the custom HTML tag this component renders as (`<app-root>`, used once in index.html)
//   - templateUrl / styleUrl: which .html and .css files make up this component's view
//   - imports: which OTHER components/directives this component's template is allowed to use
//
// That last one - `imports: [RouterOutlet]` - is the "standalone component" model. Angular used to
// require every component to be declared inside an `NgModule`, a separate class whose only job was
// listing "here are the components/directives available together." Modern Angular (17+) dropped that
// requirement: each component now just imports what it directly needs, like any other TypeScript
// import, which is simpler to reason about and is what "standalone: true" (now the default, so you
// won't even see that flag written out) means throughout this project.
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {}
