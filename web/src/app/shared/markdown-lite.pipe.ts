import { Pipe, PipeTransform, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { workItemIdCellUrl } from './work-item-id-column';

/** Renders the small subset of markdown the LLM actually writes - **bold**, *italic* or _italic_,
 * `code`, "* "/"- " bullet lists, "#" through "######" headings, and pipe tables - as real HTML
 * instead of literal asterisks/pipes/backticks/hashes (this was previously plain text
 * interpolation, so a reply like "call `get_my_work_items`" showed the backticks verbatim, and
 * "*logo missing*" showed the asterisks; "### Summary" showed the hashes literally, since nothing
 * in the system prompt asks for headings but a conversational reply produces them anyway whenever
 * the model decides a section label reads better than another paragraph).
 * Not a general markdown parser: no links, nested lists, or numbered lists, because the system
 * prompt never asks the model to produce those here - only handling what's actually used keeps
 * this small enough to read in one sitting instead of reaching for a dependency.
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

  /** `workItemBaseUrl` is the same value message-item.html uses for structured tables (e.g.
   * "https://dev.azure.com/org/Project/_workitems/edit"). Pass it and ID-column cells in a markdown
   * table become links to the work item, exactly as they already do in a deterministic flow's table.
   * Omit it (or pass null, as when the user's ADO settings haven't loaded) and those cells stay
   * plain text rather than becoming links that would 404. */
  transform(value: string | null | undefined, workItemBaseUrl: string | null = null): SafeHtml {
    return this.sanitizer.bypassSecurityTrustHtml(renderMarkdownLite(value ?? '', workItemBaseUrl));
  }
}

function renderMarkdownLite(raw: string, workItemBaseUrl: string | null = null): string {
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
      blocks.push(renderTable(lines.slice(i, tableEnd), workItemBaseUrl));
      i = tableEnd - 1;
      continue;
    }

    // "#" through "######" - the leading hashes are dropped and the level itself isn't kept, since
    // a chat reply only ever needs one visual weight of section label (see .md-heading in
    // message-item.css), not a full h1-h6 hierarchy.
    const headingMatch = /^\s*#{1,6}\s+(.*)$/.exec(line);
    if (headingMatch) {
      flushParagraph();
      flushList();
      blocks.push(`<p class="md-heading">${renderInline(headingMatch[1])}</p>`);
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
 * Requires a header row followed by a separator row of dashes - the separator is what distinguishes a
 * real table from prose that merely happens to contain pipe characters.
 *
 * Both GitHub-flavored table forms are accepted, with and without the outer pipes:
 *     | ID | Title |        ID | Title
 *     |----|-------|   and   --- | ---
 *     | 12 | Login |        12 | Login
 * Only the fenced form used to be recognized. The models writing these replies emit the bare form at
 * least as often, and it was falling through to the paragraph branch and rendering as literal pipes. */
function tryTableAt(lines: string[], start: number): number | null {
  const isRow = (line: string | undefined) => !!line && line.includes('|') && line.trim() !== '';
  const isSeparator = (line: string | undefined) => {
    if (!line || !line.includes('|')) {
      return false;
    }
    // Requiring every cell to be dashes (and at least two of them) is what keeps this from matching an
    // ordinary sentence that contains a pipe.
    const cells = splitRow(line);
    return cells.length >= 2 && cells.every((cell) => /^:?-+:?$/.test(cell));
  };

  if (!isRow(lines[start]) || !isSeparator(lines[start + 1])) {
    return null;
  }

  let end = start + 2;
  while (isRow(lines[end]) && !isSeparator(lines[end])) {
    end++;
  }
  return end;
}

function renderTable(tableLines: string[], workItemBaseUrl: string | null = null): string {
  const headers = splitRow(tableLines[0]);
  const bodyRows = tableLines.slice(2).map(splitRow);

  const head = `<tr>${headers.map((h) => `<th>${renderInline(h)}</th>`).join('')}</tr>`;
  const body = bodyRows
    .map(
      (cells) =>
        `<tr>${cells
          .map((c, column) => `<td>${renderCell(c, headers[column] ?? '', workItemBaseUrl)}</td>`)
          .join('')}</tr>`,
    )
    .join('');

  // Wrapped so a wide result set scrolls inside the message instead of stretching the whole thread,
  // matching how message-item.html already frames the structured (deterministic-flow) tables.
  return `<div class="prose-table"><table><thead>${head}</thead><tbody>${body}</tbody></table></div>`;
}

/** A work item id in the ID column becomes the same linked brass stamp used for structured tables and
 * confirmations, so an id means the same thing and is clickable everywhere it appears. Anything that
 * isn't a bare whole number is left alone - a title that happens to be numeric shouldn't turn into a
 * broken link. The value is already HTML-escaped by the time it gets here, and workItemIdCellUrl's
 * digits check means nothing but digits can reach the href - but workItemBaseUrl itself is only
 * digits-checked on the id portion, not escaped, so it's escaped here at the point of splicing into a
 * raw HTML string (unlike message-item.html's `[href]` property binding, which needs no such escaping
 * of its own - see workItemIdCellUrl's doc comment for why the escaping lives here and not there). */
function renderCell(cell: string, header: string, workItemBaseUrl: string | null): string {
  // The model frequently writes the ID cell as "**13016**" rather than a bare number - stripping the
  // emphasis markers before the bare-whole-number check is what keeps that still recognized as a
  // linkable id instead of silently falling back to plain (unlinked) bold text.
  const plainCell = stripEmphasisMarkers(cell);
  const url = workItemIdCellUrl(header, plainCell, workItemBaseUrl);
  return url
    ? `<a class="stamp" href="${escapeHtmlAttribute(url)}" target="_blank" rel="noopener">#${plainCell}</a>`
    : renderInline(cell);
}

function stripEmphasisMarkers(text: string): string {
  return text
    .trim()
    .replace(/^(\*{1,2}|_{1,2})(.*)\1$/, '$2')
    .trim();
}

/** For values interpolated directly into an HTML attribute (as opposed to escapeHtml, which runs over
 * the markdown body text) - quotes matter here specifically because they're what let a value break out
 * of a surrounding href="...". Escapes single quotes too (not just double), even though this pipe
 * always double-quotes its own href attributes, as defense-in-depth against the string ever being
 * spliced into a single-quoted context. */
function escapeHtmlAttribute(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
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
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
