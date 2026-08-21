/** Shared "is this the ID column, and if so what's the link for this cell" rule for work-item
 * tables - used both by structured tables from deterministic flows (message-item.ts, rendered via
 * message-item.html) and by markdown tables the conversational agent writes as prose
 * (markdown-lite.pipe.ts). Both previously implemented this independently and could drift; a title
 * that happens to be a whole number must never become a broken link, in either rendering path. */

export function isWorkItemIdColumn(header: string): boolean {
  return header.trim().toUpperCase() === 'ID';
}

/** Returns the work item URL for this cell, or null if it isn't a linkable ID cell (wrong column, no
 * base URL configured yet, or the value isn't a bare whole number). Deliberately returns the RAW url,
 * not HTML-attribute-escaped: message-item.html binds this via Angular's `[href]` property binding,
 * which sets the DOM property directly and needs no escaping of its own - escaping here would corrupt
 * a URL containing `&` for that caller. markdown-lite.pipe.ts is the one caller that instead splices
 * this into a raw HTML string, so IT is responsible for escaping at that point (see its renderCell). */
export function workItemIdCellUrl(
  header: string,
  cellValue: string,
  workItemBaseUrl: string | null,
): string | null {
  const id = cellValue.trim();
  if (!isWorkItemIdColumn(header) || !workItemBaseUrl || !/^\d+$/.test(id)) {
    return null;
  }
  return `${workItemBaseUrl}/${id}`;
}
