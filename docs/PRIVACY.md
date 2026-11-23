# Privacy policy

This policy covers openfno.com: the public website, the documentation under /docs, and the
OpenFNO console that runs on it. OpenFNO is source-available software. An installation that
someone else runs is covered by that operator's own policy; what any installation can send to
outside services is listed in [Self-hosting and external integrations](modules/external_integrations.md).

Effective 23 November 2026.

## Who runs openfno.com

The maintainer of the OpenFNO project runs openfno.com. For questions or requests about your
data, open an issue on [GitHub](https://github.com/helloupendra/algorithmic-trading-engine/issues)
and ask for a private contact. Do not post personal data in a public issue.

## The public website and the documentation

- There are no accounts, no advertising, and no third-party analytics or tracking scripts on the
  public pages. The site's content security policy loads scripts and fonts from openfno.com only.
- The site is served through Cloudflare, which processes connection data (IP address, browser
  user agent, the page requested) to deliver and protect the site. The server keeps ordinary
  request logs for security and fault-finding.
- Your light or dark theme choice may be kept in your browser's local storage
  (`openfno.theme`). It never leaves your browser.

## The console

The console is for invited users only. For a signed-in user it stores:

- your username, a hashed password, your role and module permissions, and your sign-in sessions;
- what you do in the console: paper strategy runs, orders and positions, settings, alerts, and an
  activity log of sign-ins and actions;
- the broker or data-provider credentials you choose to connect (for example FYERS, Dhan or
  Angel One). They are encrypted at rest and used only for the requests you set up: market data,
  paper trading and read-only portfolio reports;
- if you link Telegram, your chat identifier and the messages exchanged with the alerts and the
  assistant.

Optional AI features send selected context to hosted AI providers. The integrations page lists what
is sent and how to turn each feature off.

## Google user data

The OpenFNO server uses one Google OAuth client ("openfno") for a single purpose: to back up the
installation's own data, meaning full-database exports and the daily market-data archive, to the
Google Drive of the site's operator.

- **Permission:** `drive.file` only. The app can create files and read, update or delete only
  the files it created. It cannot see, open or change any other file in that Drive.
- **Use:** storing backups, checking them (checksums and a read-back of every row), restoring
  them, and deleting the app's own older backups under the retention rule below.
- **Sharing:** Google user data is not sold, not shared with third parties, not used for
  advertising and not used to train AI models. Nobody reads it except the operator restoring
  their own backup, or where the law requires it.
- **Retention:** backups stay until the operator removes them. The server keeps the newest eight
  full-database backups and deletes older ones it created.
- **Revoking access:** remove the app at
  [Google Account → Third-party apps](https://myaccount.google.com/permissions). Backups stop
  from that moment.

The use and transfer of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements.

## Sharing

Personal data is not sold. Data leaves the server only for the services that make the features
you use work: the hosting provider, Cloudflare, the brokers and data providers you connect,
Telegram if you link it, hosted AI if it is enabled, and the Google Drive backups described above.

## Security and retention

- Traffic to the site is encrypted (HTTPS). Stored credentials are encrypted, and access inside
  the console follows each user's role. No system is perfectly secure.
- Account data is kept while the account is active. You can ask for your account and its data to
  be deleted through the contact above. Copies in backups disappear as those backups are replaced.

## Children

openfno.com is not meant for anyone under 18.

## Changes

Changes to this policy are posted on this page with a new effective date.
