import { Pipe, PipeTransform, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

/** Renders the small subset of markdown the LLM actually writes - **bold**, *italic* or _italic_,
 * `code`, "* "/"- " bullet lists, and pipe tables - as real HTML instead of literal asterisks/pipes/
 * backticks (this was previously plain text interpolation, so a reply like "call
 * `get_my_work_items`" showed the backticks verbatim, and "*logo missing*" showed the asterisks).
 * Not a general markdown parser: no headings, links, nested lists, or numbered lists, because the
 * system prompt never asks the model to produce those here - only handling what's actually used
 * keeps this small enough to read in one sitting instead of reaching for a dependency.
 *
 * Tables matter specifically because the deterministic flows (ViewBugsFlow, MyItemsFlow) return
 * STRUCTURED table data that message-item.html renders directly, but the conversational agent writes
 * its replies as prose and is tagged Text - so when a request resolves a work item by NAME rather
 * than numeric id it goes to the agent, whose "output a markdown table" instruction previously
 * landed here as literal pipe characters.
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

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    // A table is the one construct here that spans multiple lines and can't be decided from the
    // current line alone - "| a | b |" on its own is just text until the NEXT line turns out to be a
    // |---|---| separator. So this peeks ahead one line, and on a match consumes the whole run of
    // rows itself rather than letting the per-line branches below see them.
    const tableEnd = tryTableAt(lines, i);
    if (tableEnd !== null) {
      flushParagraph();
      flushList();
      blocks.push(renderTable(lines.slice(i, tableEnd)));
      i = tableEnd - 1;
      continue;
    }

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

/** If a markdown table starts at `start`, returns the index just past its last row; otherwise null.
 * Requires a header row followed by a |---|:--:|---| separator - the separator is what distinguishes
 * a real table from prose that merely happens to contain pipe characters. */
function tryTableAt(lines: string[], start: number): number | null {
  const isRow = (line: string | undefined) => !!line && line.trim().startsWith('|');
  const isSeparator = (line: string | undefined) =>
    !!line && /^\s*\|(\s*:?-{1,}:?\s*\|)+\s*$/.test(line);

  if (!isRow(lines[start]) || !isSeparator(lines[start + 1])) {
    return null;
  }

  let end = start + 2;
  while (isRow(lines[end]) && !isSeparator(lines[end])) {
    end++;
  }
  return end;
}

function renderTable(tableLines: string[]): string {
  const headers = splitRow(tableLines[0]);
  const bodyRows = tableLines.slice(2).map(splitRow);

  const head = `<tr>${headers.map((h) => `<th>${renderInline(h)}</th>`).join('')}</tr>`;
  const body = bodyRows
    .map((cells) => `<tr>${cells.map((c) => `<td>${renderInline(c)}</td>`).join('')}</tr>`)
    .join('');

  // Wrapped so a wide result set scrolls inside the message instead of stretching the whole thread,
  // matching how message-item.html already frames the structured (deterministic-flow) tables.
  return `<div class="prose-table"><table><thead>${head}</thead><tbody>${body}</tbody></table></div>`;
}

/** Splits "| a | b |" into ["a", "b"] - the leading/trailing pipes produce empty edge entries that
 * aren't cells, so they're dropped rather than rendered as blank columns. */
function splitRow(line: string): string[] {
  const cells = line.trim().split('|');
  if (cells.length > 0 && cells[0].trim() === '') {
    cells.shift();
  }
  if (cells.length > 0 && cells[cells.length - 1].trim() === '') {
    cells.pop();
  }
  return cells.map((c) => c.trim());
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
