const fs = require('fs');
const html = fs.readFileSync('JellyfinToJellyfin/Configuration/configPage.html', 'utf8');
const m = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
if (!m) { console.log('no script'); process.exit(1); }
const js = m[1];

// Identifiers must only be scanned outside string literals, comments and regex literals: strings and
// regexes contain text like "Output folder (optional)", "rgba(0,0,0,0.1)" or /"/g which would
// otherwise look like undefined function calls.
function stripLiterals(source) {
    const valueStart = /[A-Za-z0-9_$)\]'"`]/;
    let out = '';
    let i = 0;
    let prev = '';

    while (i < source.length) {
        const ch = source[i];
        const next = source[i + 1];

        if (ch === '/' && next === '/') {
            while (i < source.length && source[i] !== '\n') { i++; }
            out += ' ';
            continue;
        }

        if (ch === '/' && next === '*') {
            i += 2;
            while (i < source.length && !(source[i] === '*' && source[i + 1] === '/')) { i++; }
            i += 2;
            out += ' ';
            continue;
        }

        if (ch === '\'' || ch === '"' || ch === '`') {
            const quote = ch;
            i++;
            while (i < source.length && source[i] !== quote) {
                if (source[i] === '\\') { i++; }
                i++;
            }
            i++;
            out += "''";
            prev = "'";
            continue;
        }

        // A '/' only starts a regex literal where a value may begin; otherwise it is division.
        if (ch === '/' && (prev === '' || !valueStart.test(prev))) {
            i++;
            let inClass = false;
            while (i < source.length) {
                const c = source[i];
                if (c === '\\') { i += 2; continue; }
                if (c === '[') { inClass = true; } else if (c === ']') { inClass = false; } else if (c === '/' && !inClass) { i++; break; } else if (c === '\n') { break; }
                i++;
            }
            while (i < source.length && /[a-z]/.test(source[i])) { i++; }
            out += "''";
            prev = "'";
            continue;
        }

        out += ch;
        if (!/\s/.test(ch)) { prev = ch; }
        i++;
    }

    return out;
}

const code = stripLiterals(js);

const defined = new Set();
let r;
const defRe = /function\s+([A-Za-z_$][\w$]*)\s*\(/g;
while ((r = defRe.exec(code))) { defined.add(r[1]); }

const calls = new Set();
const callRe = /(^|[^.\w$])([A-Za-z_$][\w$]*)\s*\(/g;
while ((r = callRe.exec(code))) { calls.add(r[2]); }

const builtins = new Set(['if', 'for', 'while', 'switch', 'catch', 'return', 'function', 'typeof', 'String', 'Number', 'Boolean', 'Array', 'Object', 'Date', 'Math', 'JSON', 'encodeURIComponent', 'decodeURIComponent', 'setTimeout', 'clearTimeout', 'parseInt', 'parseFloat', 'require', 'console']);
const undefined_ = [...calls].filter((name) => !defined.has(name) && !builtins.has(name));
console.log('defined:', [...defined].sort().join(', '));
console.log('possibly-undefined calls:', undefined_.join(', ') || '(none)');

const unused = [...defined].filter((name) => {
    const uses = code.split(new RegExp('\\b' + name.replace(/\$/g, '\\$') + '\\b', 'g')).length - 1;
    return uses <= 1;
});
console.log('unused functions:', unused.join(', ') || '(none)');

// Verify that every element id / data-attribute referenced from JS exists in the markup.
const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((x) => x[1]));
const jsIds = new Set([...js.matchAll(/\bel\('([^']+)'\)/g)].map((x) => x[1]));
const missingIds = [...jsIds].filter((id) => !ids.has(id));
console.log('missing element ids:', missingIds.join(', ') || '(none)');

const attrs = new Set([...html.matchAll(/data-(action|field|role|instance-id)="([^"]*)"/g)].map((x) => x[1] + '=' + x[2]));
const jsAttrs = [
    ...[...js.matchAll(/data-action="([^"]+)"/g)].map((x) => 'action=' + x[1]),
    ...[...js.matchAll(/data-field="([^"]+)"/g)].map((x) => 'field=' + x[1])
];
const missingAttrs = [...new Set(jsAttrs)].filter((a) => !attrs.has(a));
console.log('missing data attributes:', missingAttrs.join(', ') || '(none)');

