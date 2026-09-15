-- Give the web app's managed identity the access it needs, and nothing more.
--
-- Run this once, connected to the SlotLock database as the Entra administrator of the
-- server. The Azure Portal's Query editor is the least-setup way to do it: it signs in with
-- the same Entra account and needs nothing installed locally.
--
-- :setvar AppName  is the App Service site name, which is also the name of its identity.
-- sqlcmd expands it; in the Portal, replace it by hand.

-- FROM EXTERNAL PROVIDER means "this principal lives in Entra ID" - there is no password
-- here, because the identity proves itself with a token the platform issues.
CREATE USER [$(AppName)] FROM EXTERNAL PROVIDER;

-- The application reads, writes, and executes nothing else. It does not own the schema:
-- migrations are applied by a human with higher privileges, so a compromised app cannot
-- rewrite the tables that hold the constraints protecting it.
ALTER ROLE db_datareader ADD MEMBER [$(AppName)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppName)];
