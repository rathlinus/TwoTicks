// Runs WhatsApp Web's icon modules with a stand-in for its icon helper and
// writes what each passes to it: name, size, view box and the path elements.
const fs = require('fs');
const path = require('path');

const dir = process.argv[2];
const icons = {};
const capture = (module) => (name, w, h, viewBox, attrs, ...children) => {
  icons[module] = { name, w, h, viewBox, attrs, children };
  return null;
};

for (const file of fs.readdirSync(dir)) {
  const text = fs.readFileSync(path.join(dir, file), 'utf8');
  const re = /__d\("((?:WDSIcon|WAWeb)[\w.]*Icon[\w.]*|WDSIcon[\w.]+)",\["(?:WDS|WAWeb)SvgIconHelpers"\]/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    const module = m[1];
    if (icons[module]) continue;
    const end = text.indexOf('\n__d(', m.index + 5);
    const source = text.slice(m.index, end < 0 ? undefined : end);
    const helpers = { createWDSIcon: capture(module), createWAWebIcon: capture(module) };
    const __d = (name, deps, factory) => {
      const mod = {};
      const req = () => helpers;
      try {
        factory(globalThis, req, req, req, mod, mod, mod);
      } catch (e) {
        // Icons built some other way are left out.
      }
    };
    try {
      new Function('__d', source)(__d);
    } catch (e) {
      // A module cut off at the end of a bundle.
    }
  }
}

// Children come either as path strings or as [tag, attributes] pairs.
const out = {};
for (const [module, icon] of Object.entries(icons)) {
  const elements = [];
  const visit = (child) => {
    if (typeof child === 'string') elements.push({ tag: 'path', d: child });
    else if (Array.isArray(child) && typeof child[0] === 'string' && child[1] && typeof child[1] === 'object') {
      elements.push({ tag: child[0], ...child[1] });
      if (Array.isArray(child[2])) child[2].forEach(visit);
    } else if (Array.isArray(child)) child.forEach(visit);
  };
  icon.children.forEach(visit);
  out[module.replace(/\.react$/, '')] = { name: icon.name, w: icon.w, h: icon.h, viewBox: icon.viewBox, attrs: icon.attrs, elements };
}
fs.writeFileSync(process.argv[3], JSON.stringify(out, null, 1));
console.log(Object.keys(out).length, 'icons');
