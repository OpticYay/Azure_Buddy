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

  it('still links a plain numeric id in the ID column when workItemBaseUrl is well-formed', () => {
    const baseUrl = 'https://dev.azure.com/org/Project/_workitems/edit';
    const table = ['| ID | Title |', '|----|-------|', '| 12 | Login |'].join('\n');

    const html = (pipe.transform(table, baseUrl) as { changingThisBreaksApplicationSecurity: string })
      .changingThisBreaksApplicationSecurity;

    expect(html).toContain(`href="${baseUrl}/12"`);
  });
});
