import { Pipe, PipeTransform, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

/** Renders the small subset of markdown the LLM actually writes - **bold**, *italic*/_italic_,
 * `code`, and "* "/"- " bullet lists - as real HTML instead of literal asterisks/underscores/
 * backticks (this was previously plain text interpolation, so a reply like "call
 * `get_my_work_items`" showed the backticks verbatim, and "*logo missing*" showed the asterisks).
 * Not a general markdown parser: no headings, links, nested lists, or numbered lists, because the
 * system prompt never asks the model to produce those here - only handling what's actually used
 * keeps this small enough to read in one sitting instead of reaching for a dependency.
 *
 * HTML-escapes the raw text FIRST, before any of our own tags are added - the input is LLM output,
 * not a static template, so it must be treated as untrusted the same way user input would be. Only
 * the `<strong>`/`<code>`/`<ul>`/`<li>`/`<p>`/`<br>` tags this pipe itself builds ever reach
 * bypassSecurityTrustHtml, never anything from the source text directly.
 *
 * Emits bare `<p>`/`<ul>`/`<li>` with no classes of their own - the caller wraps the output in a
 * single `.prose`-classed container and styles these as descendants, so a multi-paragraph reply
 * doesn't produce N nested `.prose` blocks each re-asserting the same margin/color rules. */
@Pipe({ name: 'markdownLite', standalone: true })
export class MarkdownLitePipe implements PipeTransform {
  private readonly sanitizer = inject(DomSanitizer);

  transform(value: string | null | undefined): SafeHtml {
    return this.sanitizer.bypassSecurityTrustHtml(renderMarkdownLite(value ?? ''));
  }
}

function renderMarkdownLite(raw: string): string {
  const lines = escapeHtml(raw).split('\n');
  const blocks: string[] = [];
  let paragraphLines: string[] = [];
  let listItems: string[] = [];

  const flushParagraph = () => {
    if (paragraphLines.length > 0) {
      blocks.push(`<p>${paragraphLines.map(renderInline).join('<br>')}</p>`);
      paragraphLines = [];
    }
  };
  const flushList = () => {
    if (listItems.length > 0) {
      blocks.push(`<ul>${listItems.map((item) => `<li>${renderInline(item)}</li>`).join('')}</ul>`);
      listItems = [];
    }
  };

  for (const line of lines) {
    const bulletMatch = /^\s*[*-]\s+(.*)$/.exec(line);
    if (bulletMatch) {
      flushParagraph();
      listItems.push(bulletMatch[1]);
    } else if (line.trim() === '') {
      flushParagraph();
      flushList();
    } else {
      flushList();
      paragraphLines.push(line);
    }
  }
  flushParagraph();
  flushList();

  return blocks.join('');
}

/** Applied AFTER escaping, to text that's already had its literal `<`/`&` neutralized, so these
 * regexes only ever match markdown syntax, never something disguised as HTML.
 *
 * Bold is matched before single-asterisk italic so "**x**" doesn't get read as italic-around-bold
 * ("*", then "*x*", then a stray "*"). The underscore-italic regex requires a non-word character (or
 * string start/end) on both outer sides of each underscore - without that guard, every snake_case
 * identifier in a reply (`search_work_items`, `work_item_id`, ...) would have a fragment of itself
 * turned into <em> around its middle underscore. */
function renderInline(escapedLine: string): string {
  return escapedLine
    .replace(/\*\*([^*]+?)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^\s*][^*]*?)\*/g, '<em>$1</em>')
    .replace(/(?<![\w_])_([^\s_][^_]*?)_(?![\w_])/g, '<em>$1</em>')
    .replace(/`([^`]+?)`/g, '<code>$1</code>');
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}
