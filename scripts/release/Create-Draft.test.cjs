const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const createDraft = require('./Create-Draft.cjs');

function fixture(t, tag = 'v1.0.1', releases = [], assets = []) {
    const root = path.resolve(__dirname, '../../artifacts/release-script-tests');
    fs.mkdirSync(root, { recursive: true });
    const directory = fs.mkdtempSync(path.join(root, 'case-'));
    t.after(() => {
        assert.ok(path.resolve(directory).startsWith(root + path.sep));
        fs.rmSync(directory, { recursive: true, force: true });
    });
    for (const runtime of ['win-x64', 'win-x86', 'win-arm64']) {
        const name = `GithubReleaseWatch-${tag}-${runtime}.zip`;
        const bytes = Buffer.from(runtime);
        fs.writeFileSync(path.join(directory, name), bytes);
        fs.writeFileSync(path.join(directory, name + '.sha256'), `${crypto.createHash('sha256').update(bytes).digest('hex')}  ${name}\n`);
    }
    const calls = [];
    const endpoints = {
        listReleases: Symbol('releases'), listReleaseAssets: Symbol('assets'),
        createRelease: async args => { calls.push(['create', args]); return { data: { id: 1, html_url: 'https://example.test/draft' } }; },
        uploadReleaseAsset: async args => { calls.push(['upload', args]); },
        deleteReleaseAsset: async args => { calls.push(['delete', args]); },
    };
    const github = {
        rest: { repos: endpoints },
        paginate: async endpoint => {
            calls.push(['list', endpoint]);
            return endpoint === endpoints.listReleases ? releases : assets;
        },
    };
    const summary = { addHeading() { return this; }, addLink() { return this; }, addRaw() { return this; }, async write() {} };
    return { args: { github, context: { repo: { owner: 'owner', repo: 'repo' } }, core: { info() {}, summary }, directory, tag }, calls, directory };
}

test('creates an empty draft with three architecture ZIPs and checksums', async t => {
    const f = fixture(t);
    await createDraft(f.args);
    const creation = f.calls.find(([kind]) => kind === 'create')[1];
    assert.equal(creation.draft, true);
    assert.equal(creation.body, '');
    assert.equal(creation.generate_release_notes, false);
    assert.equal(creation.prerelease, false);
    assert.equal(f.calls.filter(([kind]) => kind === 'upload').length, 6);
});

test('marks preview tags as prereleases', async t => {
    const f = fixture(t, 'v1.0.2-beta.1');
    await createDraft(f.args);
    assert.equal(f.calls.find(([kind]) => kind === 'create')[1].prerelease, true);
});

test('accepts numeric tags without a v prefix', async t => {
    const f = fixture(t, '1.0.1');
    await createDraft(f.args);
    assert.equal(f.calls.find(([kind]) => kind === 'create')[1].tag_name, '1.0.1');
});

test('retry preserves edited draft details and unrelated attachments', async t => {
    const tag = 'v1.0.1';
    const draft = { id: 7, tag_name: tag, draft: true, name: 'My title', body: 'My detailed release notes', html_url: 'https://example.test/draft' };
    const f = fixture(t, tag, [draft], [{ id: 3, name: `GithubReleaseWatch-${tag}-win-x64.zip` }, { id: 4, name: 'manual.txt' }]);
    await createDraft(f.args);
    assert.equal(f.calls.filter(([kind]) => kind === 'create').length, 0);
    assert.deepEqual(f.calls.filter(([kind]) => kind === 'delete').map(([, args]) => args.asset_id), [3]);
    assert.equal(draft.name, 'My title');
    assert.equal(draft.body, 'My detailed release notes');
});

test('refuses to modify an already published release', async t => {
    const f = fixture(t, 'v1.0.1', [{ id: 1, tag_name: 'v1.0.1', draft: false }]);
    await assert.rejects(createDraft(f.args), /already published/);
    assert.equal(f.calls.filter(([kind]) => kind !== 'list').length, 0);
});

test('missing architecture package fails before any API mutation', async t => {
    const f = fixture(t);
    fs.unlinkSync(path.join(f.directory, 'GithubReleaseWatch-v1.0.1-win-arm64.zip'));
    await assert.rejects(createDraft(f.args), /ENOENT/);
    assert.equal(f.calls.length, 0);
});

test('checksum mismatch fails before any API call', async t => {
    const f = fixture(t);
    fs.writeFileSync(path.join(f.directory, 'GithubReleaseWatch-v1.0.1-win-x64.zip.sha256'), 'incorrect');
    await assert.rejects(createDraft(f.args), /Checksum mismatch/);
    assert.equal(f.calls.length, 0);
});

test('invalid tags are rejected before files or APIs are used', async () => {
    await assert.rejects(createDraft({ tag: '../invalid' }), /valid version tag/);
});
