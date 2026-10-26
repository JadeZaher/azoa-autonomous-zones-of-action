import { completeBusinessVerification, signIn, test } from '../support/fixtures'

/** A tenant administrator verifies its organization (KYB) through the hosted Identomat flow. */
test('a tenant verifies its business through the hosted flow and sees the approval', async ({ page, api, vendors }) => {
  const tenant = await api.makeTenant(await api.registerPerson('kyb'))
  await signIn(page, tenant)

  await completeBusinessVerification(page, api, vendors)
})
