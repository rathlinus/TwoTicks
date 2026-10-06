// Pulls WhatsApp Web's emoji data modules out of its script bundles and writes
// them as JSON: the ordered list that sets the sprite cells, the map of older
// spellings, and the picker categories.
const fs = require('fs');
const path = require('path');

const dir = process.argv[2];
const wanted = ['WAWebEmojiJsonWaEmojiUnicode', 'WAWebEmojiJsonWaEmojiLegacy', 'WAWebEmojiJsonWaEmojiCategory'];
const found = {};

for (const file of fs.readdirSync(dir)) {
  const text = fs.readFileSync(path.join(dir, file), 'utf8');
  for (const name of wanted) {
    if (found[name]) continue;
    const start = text.indexOf(`__d("${name}",`);
    if (start < 0) continue;
    const end = text.indexOf('\n__d(', start + 10);
    const source = text.slice(start, end < 0 ? undefined : end);
    let exports;
    const __d = (moduleName, deps, factory) => {
      const module = {};
      const require = () => ({});
      factory(globalThis, require, require, require, module, module, module);
      exports = module.default;
    };
    new Function('__d', source)(__d);
    found[name] = exports;
  }
}

for (const name of wanted) {
  if (!found[name]) throw new Error('not found: ' + name);
}
const out = {
  ordered: found.WAWebEmojiJsonWaEmojiUnicode,
  legacy: found.WAWebEmojiJsonWaEmojiLegacy,
  categories: found.WAWebEmojiJsonWaEmojiCategory,
};
fs.writeFileSync(process.argv[3], JSON.stringify(out));
const nonEmpty = out.ordered.filter(e => e !== '' && e != null).length;
console.log('ordered', out.ordered.length, 'non-empty', nonEmpty, 'legacy', Object.keys(out.legacy).length,
  'categories', Object.keys(out.categories).map(k => k + ':' + out.categories[k].length).join(' '));
