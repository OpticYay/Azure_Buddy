import { TestBed } from '@angular/core/testing';
import { MarkdownLitePipe } from './markdown-lite.pipe';

describe('MarkdownLitePipe', () => {
  let pipe: MarkdownLitePipe;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    pipe = TestBed.runInInjectionContext(() => new MarkdownLitePipe());
  });

  it('does not let a quote in workItemBaseUrl break out of the href attribute', () => {
    const maliciousBaseUrl = 'https://dev.azure.com/org"><script>alert(1)</script><a href="';
    const table = ['| ID | Title |', '|----|-------|', '| 12 | Login |'].join('\n');

    const html = (pipe.transform(table, maliciousBaseUrl) as { changingThisBreaksApplicationSecurity: string })
      .changingThisBreaksApplicationSecurity;

    expect(html).not.toContain('<script>');
    // The raw quote from workItemBaseUrl must have been escaped to &quot; rather than terminating the
    // href="..." attribute early.
    expect(html).toContain('&quot;');
    expect(html).not.toContain('href="https://dev.azure.com/org">');
  });

  function render(value: string, workItemBaseUrl: string | null = null): string {
    const safe = pipe.transform(value, workItemBaseUrl) as {
      changingThisBreaksApplicationSecurity: string;
    };
    return safe.changingThisBreaksApplicationSecurity;
  }

  it('renders bold text', () => {
    expect(render('call **get_my_work_items**')).toContain('<strong>get_my_work_items</strong>');
  });

  it('renders italic text with asterisks', () => {
    expect(render('*logo missing*')).toContain('<em>logo missing</em>');
  });

  it('renders italic text with underscores but leaves snake_case identifiers alone', () => {
    expect(render('_italic_')).toContain('<em>italic</em>');
    expect(render('search_work_items')).not.toContain('<em>');
    expect(render('search_work_items')).toContain('search_work_items');
  });

  it('renders inline code', () => {
    expect(render('use `get_my_work_items`')).toContain('<code>get_my_work_items</code>');
  });

  it('strips heading hashes and tags the line as a heading', () => {
    const html = render('### Summary');
    expect(html).not.toContain('#');
    expect(html).toContain('class="md-heading"');
    expect(html).toContain('Summary');
  });

  it('renders "* " and "- " lines as a bullet list', () => {
    const html = render('* first\n* second');
    expect(html).toContain('<ul>');
    expect(html).toContain('<li>first</li>');
    expect(html).toContain('<li>second</li>');
  });

  it('renders a fenced GFM table (with outer pipes)', () => {
    const html = render('| ID | Title |\n|----|-------|\n| 12 | Login |');
    expect(html).toContain('<table>');
    expect(html).toContain('<th>ID</th>');
    expect(html).toContain('<td>12</td>');
    expect(html).toContain('<td>Login</td>');
  });

  it('renders a bare GFM table (without outer pipes)', () => {
    const html = render('ID | Title\n--- | ---\n12 | Login');
    expect(html).toContain('<table>');
    expect(html).toContain('<th>ID</th>');
    expect(html).toContain('<td>12</td>');
  });

  it('links numeric ID-column cells to the work item when workItemBaseUrl is provided', () => {
    const html = render(
      '| ID | Title |\n|----|-------|\n| 12 | Login |',
      'https://dev.azure.com/org/Proj/_workitems/edit',
    );
    expect(html).toContain('href="https://dev.azure.com/org/Proj/_workitems/edit/12"');
    expect(html).toContain('#12');
  });

  it('leaves ID-column cells as plain text when workItemBaseUrl is null', () => {
    const html = render('| ID | Title |\n|----|-------|\n| 12 | Login |');
    expect(html).not.toContain('<a ');
    expect(html).toContain('<td>12</td>');
  });

  it('does not link a non-numeric ID-column cell', () => {
    const html = render(
      '| ID | Title |\n|----|-------|\n| N/A | Login |',
      'https://dev.azure.com/org/Proj/_workitems/edit',
    );
    expect(html).not.toContain('<a ');
  });

  it('escapes a double quote in workItemBaseUrl so it cannot break out of the href attribute', () => {
    const maliciousBaseUrl = 'https://dev.azure.com"><script>alert(1)</script>';
    const html = render('| ID | Title |\n|----|-------|\n| 12 | Login |', maliciousBaseUrl);

    // The malicious base URL is attacker-controlled input for this test's purposes; renderCell must not
    // let a raw `"` terminate the href attribute early and inject a sibling tag.
    expect(html).not.toContain('<script>');
    expect(html).not.toMatch(/href="[^"]*"[^>]*>[^<]*<script/);
  });

  it('HTML-escapes raw angle brackets and ampersands in the source text', () => {
    const html = render('<img src=x onerror=alert(1)> & more');
    expect(html).not.toContain('<img');
    expect(html).toContain('&lt;img');
    expect(html).toContain('&amp;');
  });
});
