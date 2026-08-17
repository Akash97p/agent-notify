#!/usr/bin/env python3
"""
Dependency-free Markdown -> HTML renderer and site generator for AgentNotify docs.
Usage: python3 scripts/render-docs.py <repo-root> <output-dir>
"""
import sys
import os
import re
import html
import posixpath


# Page set and navigation, defined as one table so a contributor can add a page in one line.
# (source path, slug, title, section)
# Source document, published slug, page title, sidebar section, one-line blurb for the index.
PAGES = [
    ("docs/INSTALLATION.md", "installation", "Install on Windows", "Getting started",
     "The Windows installer, what setup writes, and how to uninstall."),
    ("docs/INSTALLATION_UNIX.md", "installation-unix", "Install on macOS and Linux", "Getting started",
     "Installing the CLI and the headless broker, with systemd and launchd units."),
    ("docs/TROUBLESHOOTING.md", "troubleshooting", "Troubleshooting", "Getting started",
     "Symptoms, causes, and fixes for the problems people actually hit."),
    ("docs/CLI.md", "cli", "Command line", "Using AgentNotify",
     "Every command, flag, output shape, and exit code of the agentnotify CLI."),
    ("docs/API.md", "api", "Local REST API", "Using AgentNotify",
     "The loopback /v1 HTTP API: routes, bodies, authentication, and errors."),
    ("docs/CONFIGURATION.md", "configuration", "Configuration", "Using AgentNotify",
     "The on-disk config file, every setting and its default, and custom notification types."),
    ("docs/CHANNELS.md", "channels", "Outbound channels", "Using AgentNotify",
     "The eighteen opt-in delivery adapters and the security policy each one applies."),
    ("docs/AGENT_INTEGRATION.md", "agent-integration", "Agent integration", "Agents",
     "How an agent should send, key, and resolve notifications."),
    ("docs/AGENT_SKILLS.md", "agent-skills", "Agent skills", "Agents",
     "Installing the distributable SKILL.md into agents that support skills."),
    ("docs/ARCHITECTURE.md", "architecture", "Architecture", "Project",
     "Project layout, process model, and the boundaries between components."),
    ("docs/CROSS_PLATFORM.md", "cross-platform", "Cross-platform plan", "Project",
     "The macOS and Linux plan, its phases, and what is done so far."),
    ("docs/ROADMAP.md", "roadmap", "Roadmap", "Project",
     "Direction, and the things explicitly not committed to."),
    ("docs/FEATURE_BACKLOG.md", "feature-backlog", "Feature backlog", "Project",
     "The ordered backlog with per-item status."),
    ("docs/RELEASING.md", "releasing", "Releasing", "Project",
     "Version scheme, tagging, and the release and Pages workflows."),
    ("docs/VERIFICATION.md", "verification", "Verification record", "Project",
     "What has actually been verified, and what remains unverified."),
    ("docs/BUG.md", "bugs", "Bug log", "Project",
     "Defects found after a capability was called complete, and what caused them."),
]

GITHUB_BASE = "https://github.com/Akash97p/agent-notify/blob/main/"
GITHUB_TREE_DOCS = "https://github.com/Akash97p/agent-notify/tree/main/docs"


def slugify(text, seen):
    s = text.lower()
    # replace non-alphanumerics to hyphens
    s = re.sub(r'[^a-z0-9]+', '-', s)
    s = re.sub(r'-+', '-', s).strip('-')
    if not s:
        s = "section"
    base = s
    slug = base
    c = 1
    while slug in seen:
        slug = f"{base}-{c}"
        c += 1
    seen.add(slug)
    return slug


def is_table_separator(line):
    stripped = line.strip()
    # remove outer pipes for inspection
    # split by |
    # strip outer pipes then split
    t = stripped
    if t.startswith("|"):
        t = t[1:]
    if t.endswith("|"):
        t = t[:-1]
    if not t.strip():
        return False
    cells = [c.strip() for c in t.split("|")]
    if not cells:
        return False
    for cell in cells:
        if not re.match(r'^:?-+:?$', cell):
            return False
    return any("-" in c for c in cells)


def split_row(line):
    t = line.strip()
    if t.startswith("|"):
        t = t[1:]
    if t.endswith("|"):
        t = t[:-1]
    cells = [c.strip() for c in t.split("|")]
    return cells


def rewrite_url(url, source_path, published_map):
    if url.startswith("http://") or url.startswith("https://") or url.startswith("mailto:") or url.startswith("#"):
        return url
    # fragment
    if "#" in url:
        path_part, frag_body = url.split("#", 1)
        frag = "#" + frag_body
    else:
        path_part, frag = url, ""
    if path_part == "":
        return url
    source_dir = posixpath.dirname(source_path)
    # handle absolute from repo root
    if path_part.startswith("/"):
        norm_joined = posixpath.normpath(path_part.lstrip("/"))
    else:
        joined = posixpath.join(source_dir, path_part) if source_dir else path_part
        norm_joined = posixpath.normpath(joined)
    # stripped candidate for repo-relative like docs/CLI.md
    stripped = path_part
    while stripped.startswith("./"):
        stripped = stripped[2:]
    stripped_norm = posixpath.normpath(stripped.lstrip("/"))
    target_slug = None
    if norm_joined in published_map:
        target_slug = published_map[norm_joined]
    elif stripped_norm in published_map:
        target_slug = published_map[stripped_norm]
    if target_slug:
        return f"{target_slug}.html{frag}"
    else:
        # unpublished -> GitHub absolute
        # avoid returning "." or empty
        if norm_joined in (".", ""):
            # fallback to stripped
            norm_joined = stripped_norm
        return f"{GITHUB_BASE}{norm_joined}{frag}"


def inline_to_html(text, source_path, published_map):
    """Render inline markdown.

    Code spans and links are replaced by placeholders first so that their contents are never
    reinterpreted as markup. Emphasis is then matched over the placeholder-bearing string, which is
    what lets ``**a `code` span**`` and ``[**bold** link](x)`` render instead of leaking asterisks.
    """
    code_placeholders = {}
    link_placeholders = {}
    placeholder_split = re.compile(r'(\x00[CL]\d+\x00)')
    # A delimiter run may not be padded with whitespace, so "a * b * c" stays literal.
    emphasis = re.compile(
        r'\*\*(?!\s)(.+?)(?<!\s)\*\*'
        r'|(?<!\w)\*(?!\s)([^*]+?)(?<!\s)\*(?!\w)'
        r'|(?<!\w)_(?!\s)([^_]+?)(?<!\s)_(?!\w)',
        # Emphasis may span the soft line breaks inside a single block, which the documents use
        # heavily because they are hard-wrapped at 100 columns.
        re.DOTALL)

    def repl_code(m):
        ph = f"\x00C{len(code_placeholders)}\x00"
        code_placeholders[ph] = f"<code>{html.escape(m.group(1), quote=False)}</code>"
        return ph

    def restore(segment):
        """Escape a segment and put back any code/link HTML it contains."""
        out = []
        for part in placeholder_split.split(segment):
            if part in code_placeholders:
                out.append(code_placeholders[part])
            elif part in link_placeholders:
                out.append(link_placeholders[part])
            else:
                out.append(html.escape(part, quote=False))
        return "".join(out)

    def markup(segment):
        out = []
        last = 0
        for m in emphasis.finditer(segment):
            out.append(restore(segment[last:m.start()]))
            if m.group(1) is not None:
                out.append(f"<strong>{restore(m.group(1))}</strong>")
            else:
                inner = m.group(2) if m.group(2) is not None else m.group(3)
                out.append(f"<em>{restore(inner)}</em>")
            last = m.end()
        out.append(restore(segment[last:]))
        return "".join(out)

    def repl_link(m):
        rewritten = rewrite_url(m.group(2).strip(), source_path, published_map)
        ph = f"\x00L{len(link_placeholders)}\x00"
        link_placeholders[ph] = (
            f'<a href="{html.escape(rewritten, quote=True)}">{markup(m.group(1))}</a>')
        return ph

    temp = re.sub(r'`([^`]+?)`', repl_code, text)
    temp = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', repl_link, temp)
    return markup(temp)


ITEM_START = re.compile(r'^(\s*)([-*]|\d+\.)\s+')


def join_item_continuations(lines):
    """Fold a list item's wrapped lines back into one line.

    The documents are hard-wrapped at 100 columns, so an item's emphasis, link or code span
    routinely straddles a line break. Rendering each physical line on its own would cut those
    spans in half and leak the raw markers.
    """
    merged = []
    open_item = False
    for raw in lines:
        if raw.strip() == "":
            merged.append(raw)
            open_item = False
        elif ITEM_START.match(raw):
            merged.append(raw)
            open_item = True
        elif open_item:
            merged[-1] = merged[-1].rstrip() + " " + raw.strip()
        else:
            merged.append(raw)
    return merged


def parse_list_lines(lines, source_path, published_map):
    html_parts = []
    stack = []  # each {'indent':int,'tag':str,'open_li':bool}
    for raw in join_item_continuations(lines):
        if raw.strip() == "":
            continue
        m = re.match(r'^(\s*)([-*]|\d+\.)\s+(.*)', raw)
        if m:
            indent = len(m.group(1).replace('\t', '    '))
            marker = m.group(2)
            content = m.group(3)
            tag = "ul" if marker in ("-", "*") else "ol"
            while stack and indent < stack[-1]["indent"]:
                if stack[-1]["open_li"]:
                    html_parts.append("</li>")
                    stack[-1]["open_li"] = False
                html_parts.append(f"</{stack[-1]['tag']}>")
                stack.pop()
            if not stack:
                html_parts.append(f"<{tag}>")
                stack.append({"indent": indent, "tag": tag, "open_li": False})
            elif indent > stack[-1]["indent"]:
                html_parts.append(f"<{tag}>")
                stack.append({"indent": indent, "tag": tag, "open_li": False})
            elif indent == stack[-1]["indent"]:
                if tag != stack[-1]["tag"]:
                    if stack[-1]["open_li"]:
                        html_parts.append("</li>")
                        stack[-1]["open_li"] = False
                    html_parts.append(f"</{stack[-1]['tag']}>")
                    stack.pop()
                    html_parts.append(f"<{tag}>")
                    stack.append({"indent": indent, "tag": tag, "open_li": False})
                else:
                    if stack[-1]["open_li"]:
                        html_parts.append("</li>")
                        stack[-1]["open_li"] = False
            inner = inline_to_html(content, source_path, published_map)
            html_parts.append(f"<li>{inner}")
            stack[-1]["open_li"] = True
        else:
            # continuation line
            if stack and stack[-1]["open_li"]:
                cont = raw.strip()
                if cont:
                    inner = inline_to_html(cont, source_path, published_map)
                    html_parts.append(f" {inner}")
            else:
                html_parts.append(f"<p>{html.escape(raw, quote=False)}</p>")
    while stack:
        if stack[-1]["open_li"]:
            html_parts.append("</li>")
            stack[-1]["open_li"] = False
        html_parts.append(f"</{stack[-1]['tag']}>")
        stack.pop()
    return "".join(html_parts)


def parse_markdown(text, source_path, published_map):
    lines = text.splitlines()
    html_parts = []
    headings = []  # (level, raw_text, slug)
    seen = set()
    i = 0
    while i < len(lines):
        line = lines[i]
        if line.strip() == "":
            i += 1
            continue
        # fenced code
        m = re.match(r'^\s*```\s*(\w*)\s*$', line)
        if m:
            lang = m.group(1).strip()
            code_lines = []
            i += 1
            while i < len(lines) and not re.match(r'^\s*```\s*$', lines[i]):
                code_lines.append(lines[i])
                i += 1
            if i < len(lines):
                i += 1  # consume closing
            code_content = "\n".join(code_lines)
            escaped = html.escape(code_content, quote=False)
            if lang:
                lang_esc = html.escape(lang, quote=True)
                html_parts.append(f'<pre><code class="language-{lang_esc}">{escaped}</code></pre>')
            else:
                html_parts.append(f'<pre><code>{escaped}</code></pre>')
            continue
        # heading
        hm = re.match(r'^(#{1,6})\s+(.*?)\s*(?:#+\s*)?$', line)
        if hm:
            level = len(hm.group(1))
            raw_text = hm.group(2).strip()
            slug = slugify(raw_text, seen)
            inner = inline_to_html(raw_text, source_path, published_map)
            html_parts.append(f'<h{level} id="{slug}">{inner}</h{level}>')
            headings.append((level, raw_text, slug))
            i += 1
            continue
        # horizontal rule
        if re.match(r'^\s*-{3,}\s*$', line) or re.match(r'^\s*\*{3,}\s*$', line) or re.match(r'^\s*_{3,}\s*$', line):
            html_parts.append('<hr>')
            i += 1
            continue
        # table
        if '|' in line and i + 1 < len(lines) and is_table_separator(lines[i + 1]):
            header_cells = split_row(line)
            i += 2
            body_rows = []
            while i < len(lines) and lines[i].strip() != "" and '|' in lines[i]:
                # if next line is separator? not after header
                body_rows.append(split_row(lines[i]))
                i += 1
            def render_cells(cells, is_header):
                tag = "th" if is_header else "td"
                parts = []
                for c in cells:
                    inner = inline_to_html(c, source_path, published_map)
                    parts.append(f"<{tag}>{inner}</{tag}>")
                return "".join(parts)
            html_parts.append('<div class="table-wrap"><table>')
            html_parts.append('<thead><tr>' + render_cells(header_cells, True) + '</tr></thead>')
            if body_rows:
                html_parts.append('<tbody>')
                for r in body_rows:
                    html_parts.append('<tr>' + render_cells(r, False) + '</tr>')
                html_parts.append('</tbody>')
            html_parts.append('</table></div>')
            continue
        # blockquote
        if line.lstrip().startswith('>'):
            bq_lines = []
            while i < len(lines):
                cur = lines[i]
                if cur.strip() == "":
                    # peek
                    if i + 1 < len(lines) and lines[i + 1].lstrip().startswith('>'):
                        bq_lines.append("")
                        i += 1
                        continue
                    else:
                        break
                if cur.lstrip().startswith('>'):
                    stripped = cur.lstrip()
                    content = stripped[1:]
                    if content.startswith(' '):
                        content = content[1:]
                    bq_lines.append(content)
                    i += 1
                else:
                    break
            # handle multiple paragraphs inside blockquote
            if "" in bq_lines:
                paras = []
                cur = []
                for l in bq_lines:
                    if l == "":
                        if cur:
                            paras.append(" ".join(cur))
                            cur = []
                    else:
                        cur.append(l)
                if cur:
                    paras.append(" ".join(cur))
                inner_html = "".join(f"<p>{inline_to_html(p, source_path, published_map)}</p>" for p in paras)
                html_parts.append(f"<blockquote>{inner_html}</blockquote>")
            else:
                inner_text = " ".join([l for l in bq_lines if l != ""])
                inner_html = inline_to_html(inner_text, source_path, published_map)
                html_parts.append(f"<blockquote><p>{inner_html}</p></blockquote>")
            continue
        # list
        if re.match(r'^\s*([-*]|\d+\.)\s+', line):
            list_lines = []
            while i < len(lines):
                cur = lines[i]
                if cur.strip() == "":
                    if i + 1 < len(lines) and re.match(r'^\s*([-*]|\d+\.)\s+', lines[i + 1]):
                        list_lines.append(cur)
                        i += 1
                        continue
                    else:
                        break
                if re.match(r'^\s*([-*]|\d+\.)\s+', cur):
                    list_lines.append(cur)
                    i += 1
                elif cur.startswith("  ") or cur.startswith("\t"):
                    list_lines.append(cur)
                    i += 1
                else:
                    break
            html_parts.append(parse_list_lines(list_lines, source_path, published_map))
            continue
        # paragraph: collect consecutive lines that are not block starters
        para_lines = []
        while i < len(lines):
            cur = lines[i]
            if cur.strip() == "":
                break
            if re.match(r'^\s*```', cur):
                break
            if re.match(r'^(#{1,6})\s+', cur):
                break
            if re.match(r'^\s*-{3,}\s*$', cur) or re.match(r'^\s*\*{3,}\s*$', cur) or re.match(r'^\s*_{3,}\s*$', cur):
                break
            if cur.lstrip().startswith('>'):
                break
            if re.match(r'^\s*([-*]|\d+\.)\s+', cur):
                break
            if '|' in cur and i + 1 < len(lines) and is_table_separator(lines[i + 1]):
                break
            para_lines.append(cur.strip())
            i += 1
            # if next line is blank, break after? loop will handle blank at outer
        if para_lines:
            para_text = " ".join(para_lines)
            inner = inline_to_html(para_text, source_path, published_map)
            html_parts.append(f"<p>{inner}</p>")
            continue
        # fallback
        html_parts.append(f"<p>{html.escape(line, quote=False)}</p>")
        i += 1
    return "\n".join(html_parts), headings


def build_sidebar(current_slug):
    # Group pages by section preserving order
    sections = []
    section_map = {}
    for src, slug, title, section, _blurb in PAGES:
        if section not in section_map:
            section_map[section] = []
            sections.append(section)
        section_map[section].append((slug, title))
    parts = []
    for sec in sections:
        parts.append(f'<div class="nav-section"><h3>{html.escape(sec)}</h3><ul>')
        for slug, title in section_map[sec]:
            href = f"{slug}.html"
            title_esc = html.escape(title)
            if slug == current_slug:
                parts.append(f'<li><a href="{href}" class="current" aria-current="page">{title_esc}</a></li>')
            else:
                parts.append(f'<li><a href="{href}">{title_esc}</a></li>')
        parts.append('</ul></div>')
    return "\n".join(parts)


def plain_inline(text):
    """Heading text with its markup removed, for the on-page table of contents."""
    text = re.sub(r'\[([^\]]+)\]\([^)]+\)', r'\1', text)
    text = text.replace('`', '')
    text = re.sub(r'\*\*(.+?)\*\*', r'\1', text, flags=re.DOTALL)
    text = re.sub(r'(?<!\w)([*_])(?!\s)(.+?)(?<!\s)\1(?!\w)', r'\2', text, flags=re.DOTALL)
    return html.escape(text, quote=False)


def build_toc(headings):
    h2 = [(t, s) for (lvl, t, s) in headings if lvl == 2]
    if len(h2) < 2:
        return ""
    items = []
    for text, slug in h2:
        esc = plain_inline(text)
        items.append(f'<li><a href="#{slug}">{esc}</a></li>')
    inner = "".join(items)
    return f'<nav class="toc" aria-label="On this page"><h2>On this page</h2><ul>{inner}</ul></nav>'


def main():
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <repo-root> <output-dir>", file=sys.stderr)
        sys.exit(1)
    repo_root = sys.argv[1]
    out_dir = sys.argv[2]
    template_path = os.path.join(repo_root, "site", "templates", "page.html")
    if not os.path.isfile(template_path):
        print(f"Template not found: {template_path}", file=sys.stderr)
        sys.exit(1)
    with open(template_path, "r", encoding="utf-8") as f:
        template = f.read()
    # verify required placeholders present (warn if missing)
    for ph in ("{{TITLE}}", "{{CONTENT}}", "{{SIDEBAR}}", "{{TOC}}", "{{ROOT}}", "{{SOURCE_URL}}"):
        if ph not in template:
            print(f"Warning: placeholder {ph} not found in template", file=sys.stderr)

    published_map = {src: slug for (src, slug, _, _, _) in PAGES}

    docs_out = os.path.join(out_dir, "docs")
    os.makedirs(docs_out, exist_ok=True)

    # Generate each configured page
    for src, slug, title, section, _blurb in PAGES:
        src_path = os.path.join(repo_root, src)
        if not os.path.isfile(src_path):
            print(f"Source not found: {src_path}", file=sys.stderr)
            sys.exit(1)
        with open(src_path, "r", encoding="utf-8") as f:
            md_text = f.read()
        html_content, headings = parse_markdown(md_text, src, published_map)
        sidebar = build_sidebar(slug)
        toc = build_toc(headings)
        root = "../"
        source_url = GITHUB_BASE + src
        # Title for page: use defined title
        title_esc = html.escape(title, quote=False)
        page = template
        page = page.replace("{{TITLE}}", title_esc)
        page = page.replace("{{CONTENT}}", html_content)
        page = page.replace("{{SIDEBAR}}", sidebar)
        page = page.replace("{{TOC}}", toc)
        page = page.replace("{{ROOT}}", root)
        page = page.replace("{{SOURCE_URL}}", html.escape(source_url, quote=True))
        out_path = os.path.join(docs_out, slug + ".html")
        with open(out_path, "w", encoding="utf-8") as out:
            out.write(page)

    # Generate docs/index.html
    # Build index content: intro + cards per section
    sections = []
    section_map = {}
    for src, slug, title, section, blurb in PAGES:
        if section not in section_map:
            section_map[section] = []
            sections.append(section)
        section_map[section].append((slug, title, blurb))
    index_parts = []
    index_parts.append("<h1>Documentation</h1>")
    index_parts.append(
        "<p>AgentNotify is a local human-attention broker for coding agents: agents post a small "
        "authenticated notification, and it becomes desktop attention, local history, and optional "
        "outbound delivery. Everything below is generated from the Markdown in the repository, so "
        "the site and the source cannot drift apart.</p>")
    index_parts.append(
        '<p class="docs-callout"><strong>The graphical application is Windows-only today.</strong> '
        "macOS and Linux run the same CLI, loopback API, history and outbound adapters through the "
        "headless <code>agentnotifyd</code> broker, with no tray icon and no Settings window. See "
        '<a href="installation-unix.html">Install on macOS and Linux</a> and the '
        '<a href="cross-platform.html">cross-platform plan</a>.</p>')
    for sec in sections:
        index_parts.append(f'<section class="docs-index-section"><h2>{html.escape(sec)}</h2><ul>')
        for slug, title, blurb in section_map[sec]:
            href = f"{slug}.html"
            index_parts.append(
                f'<li><a href="{href}">{html.escape(title)}</a>'
                f'<span>{html.escape(blurb)}</span></li>')
        index_parts.append('</ul></section>')
    index_content = "\n".join(index_parts)
    sidebar = build_sidebar(None)
    # index has no headings, so TOC empty
    toc = ""
    root = "../"
    source_url = GITHUB_TREE_DOCS
    title_esc = html.escape("Documentation", quote=False)
    page = template
    page = page.replace("{{TITLE}}", title_esc)
    page = page.replace("{{CONTENT}}", index_content)
    page = page.replace("{{SIDEBAR}}", sidebar)
    page = page.replace("{{TOC}}", toc)
    page = page.replace("{{ROOT}}", root)
    page = page.replace("{{SOURCE_URL}}", html.escape(source_url, quote=True))
    out_path = os.path.join(docs_out, "index.html")
    with open(out_path, "w", encoding="utf-8") as out:
        out.write(page)

    print(f"Generated {len(PAGES)} pages plus index at {docs_out}")


if __name__ == "__main__":
    main()
