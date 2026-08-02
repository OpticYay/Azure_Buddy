import { Component } from '@angular/core';

// This one uses a plain CSS @keyframes loop (see typing-indicator.css) rather than
// @angular/animations, deliberately - Angular's animation triggers describe a transition between
// discrete states (or a one-off enter/leave), which is the right tool for "message appears" or
// "sidebar collapses." An indefinitely-repeating bounce with no start/end state to trigger from is
// simpler and cheaper as a plain CSS animation - there's no state change here for Angular's animation
// system to hook into, just "keep looping while this element exists."
@Component({
  selector: 'app-typing-indicator',
  imports: [],
  templateUrl: './typing-indicator.html',
  styleUrl: './typing-indicator.css',
})
export class TypingIndicator {}
