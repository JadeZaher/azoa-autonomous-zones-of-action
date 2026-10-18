import { redirect } from 'next/navigation'

/** Operator-policy board; it lives in the operator console (QA M1, see `src/app/operator/AGENTS.md` §money-boards). */
export default function MovedToOperatorConsole() {
  redirect('/operator/sagas')
}
