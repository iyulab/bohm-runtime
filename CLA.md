# bohm-runtime Individual Contributor License Agreement (v1)

bohm-runtime is dual-licensed: AGPL-3.0 for open-source use, and a separate commercial license for
organizations that cannot adopt AGPL-3.0 terms. Offering both requires that the project hold, for
every line it ships, the rights to distribute that line under either set of terms. A contribution
whose rights were never granted can keep the file it touches out of the commercial offering — so
the rights have to be settled before the contribution is merged, not after. That is what this
agreement does, and it is why a Developer Certificate of Origin sign-off is not enough here: a DCO
certifies the origin of a contribution, it does not grant anyone the right to relicense it.

**This agreement does not transfer ownership.** You keep the copyright in your contribution. You
grant iyulab the licenses described below. It is adapted from the Apache Software Foundation
Individual Contributor License Agreement v2.0.

If you are contributing as part of your employment, or your employer otherwise holds rights in
your work, see [Contributing on behalf of an organization](#contributing-on-behalf-of-an-organization)
before you sign — this individual agreement may not be enough on its own.

## Definitions

- **"You"** means the individual who signs this agreement.
- **"Contribution"** means any original work of authorship — including any modification of, or
  addition to, an existing work — that You intentionally submit to iyulab for inclusion in
  bohm-runtime.
- **"Submit"** means any form of communication sent to iyulab or its representatives, including
  pull requests, issues, patches and discussion on any medium used to manage bohm-runtime, excluding
  communication You conspicuously mark "Not a Contribution".

## 1. Copyright license

You grant iyulab, and to recipients of software distributed by iyulab, a perpetual, worldwide,
non-exclusive, no-charge, royalty-free, irrevocable copyright license to reproduce, prepare
derivative works of, publicly display, publicly perform, sublicense and distribute Your
Contributions and such derivative works.

## 2. Distribution under both sets of license terms

For the avoidance of doubt, the license granted in section 1 expressly includes the right for
iyulab to distribute and sublicense Your Contribution — on its own or as part of bohm-runtime, in
source or object form, with or without modification — under any license terms iyulab chooses,
including both the GNU Affero General Public License version 3 and proprietary commercial license
terms, and to do so without any obligation of accounting or payment to You.

This section is the reason the agreement exists. Sections 1 and 3 alone would leave it arguable;
stating it removes the argument.

## 3. Patent license

You grant iyulab, and to recipients of software distributed by iyulab, a perpetual, worldwide,
non-exclusive, no-charge, royalty-free, irrevocable (except as stated in this section) patent
license to make, have made, use, offer to sell, sell, import and otherwise transfer Your
Contribution, where such license applies only to those patent claims licensable by You that are
necessarily infringed by Your Contribution alone or by combination of Your Contribution with
bohm-runtime.

If any entity institutes patent litigation against You or any other entity alleging that Your
Contribution, or bohm-runtime to which You contributed, constitutes direct or contributory patent
infringement, then any patent licenses granted to that entity under this agreement for that
Contribution or work terminate as of the date such litigation is filed.

## 4. Your representations

1. You are legally entitled to grant the licenses above. If your employer has rights to
   intellectual property that You create, You represent that You have received permission to make
   the Contribution on behalf of that employer, that your employer has waived such rights, or that
   your employer has executed a separate agreement with iyulab covering your contributions.
2. Each Contribution is Your original creation.
3. Your Contribution includes complete details of any third-party license or other restriction
   (including related patents and trademarks) of which You are personally aware and which are
   associated with any part of Your Contribution.

## 5. No warranty, no obligation to use

You provide Your Contributions on an "AS IS" basis, without warranties or conditions of any kind,
express or implied, including any warranty of merchantability or fitness for a particular purpose.

iyulab is under no obligation to accept, merge, use or distribute any Contribution.

## 6. Keeping the record accurate

You agree to notify iyulab of any facts or circumstances of which You become aware that would make
these representations inaccurate in any respect.

## Signing

Signing is a file you add in your own pull request. There is no external service and no account to
create.

1. Read this agreement.
2. Add `.github/cla/v1/<your-github-login>.md` — **lowercase** the login, because the check runs on
   a case-sensitive filesystem.
3. Use this content, filled in:

   ```markdown
   # CLA signature

   I have read the bohm-runtime Individual Contributor License Agreement v1 (`CLA.md`) and I agree to
   it for all of my contributions to bohm-runtime, past and future.

   - GitHub login: <your-login>
   - Name: <your full name>
   - Email: <your email>
   - Date: <YYYY-MM-DD>
   ```

4. Include that file in the pull request with your contribution. The automated CLA check looks for
   it and explains what is missing if it is not there.

Signing once covers your later contributions for as long as this version of the agreement is
current. If the agreement is revised, the new version gets its own directory (`v2/`) and asks for a
fresh signature — old signatures are not silently carried over to terms you never read.

Members of the iyulab organization are not asked to sign: their contributions are already made
under iyulab's copyright, so there is nothing left to license.

## Contributing on behalf of an organization

If your employer holds rights in your work, or you are contributing on behalf of a company, a
corporate agreement is needed in addition to — or instead of — this one. Open an issue before you
invest time in the contribution and we will sort out which applies.
