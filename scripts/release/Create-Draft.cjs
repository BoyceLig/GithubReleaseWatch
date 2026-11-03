const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

module.exports = async function createDraft({ github, context, core, directory = 'artifacts/releases', tag = process.env.RELEASE_TAG }) {
    if (!/^v?\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/.test(tag || '')) {
        throw new Error('A valid version tag is required.');
    }
    const names = ['win-x64', 'win-x86', 'win-arm64'].flatMap(runtime => {
        const name = `GithubReleaseWatch-${tag}-${runtime}.zip`;
        return [name, `${name}.sha256`];
    });
    // Validate the complete set before creating or modifying a release.
    const files = names.map(name => ({ name, data: fs.readFileSync(path.join(directory, name)) }));
    for (let i = 0; i < files.length; i += 2) {
        const digest = crypto.createHash('sha256').update(files[i].data).digest('hex');
        if (files[i + 1].data.toString('ascii').trim() !== `${digest}  ${files[i].name}`) {
            throw new Error(`Checksum mismatch: ${files[i].name}`);
        }
    }

    const repo = context.repo;
    const releases = await github.paginate(github.rest.repos.listReleases, { ...repo, per_page: 100 });
    if (releases.some(release => release.tag_name === tag && !release.draft)) {
        throw new Error(`${tag} is already published. Published releases will not be changed.`);
    }
    let release = releases.find(release => release.tag_name === tag && release.draft);
    if (!release) {
        ({ data: release } = await github.rest.repos.createRelease({
            ...repo,
            tag_name: tag,
            name: `GithubReleaseWatch ${tag}`,
            body: '',
            draft: true,
            prerelease: tag.includes('-'),
            generate_release_notes: false,
        }));
    }

    const assets = await github.paginate(github.rest.repos.listReleaseAssets, { ...repo, release_id: release.id, per_page: 100 });
    for (const file of files) {
        const existing = assets.find(asset => asset.name === file.name);
        if (existing) await github.rest.repos.deleteReleaseAsset({ ...repo, asset_id: existing.id });
        await github.rest.repos.uploadReleaseAsset({
            ...repo,
            release_id: release.id,
            name: file.name,
            data: file.data,
            headers: { 'content-type': file.name.endsWith('.zip') ? 'application/zip' : 'text/plain', 'content-length': file.data.length },
        });
    }
    // Existing draft title, description and other user-written fields are preserved.
    core.info(`Draft release is ready: ${release.html_url}`);
    await core.summary.addHeading('Draft release ready')
        .addLink('填写详情并发布', release.html_url)
        .addRaw('\n\n已上传 x64、x86、ARM64 压缩包及 SHA-256 校验文件。\n')
        .write();
};
