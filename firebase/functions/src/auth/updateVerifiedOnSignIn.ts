import {beforeUserSignedIn} from "firebase-functions/v2/identity";
import {getDatabase} from "firebase-admin/database";
import {getApps, initializeApp} from "firebase-admin/app";
import * as logger from "firebase-functions/logger";

if (getApps().length === 0) initializeApp();

/**
 * Автоматически ставит users/{uid}/emailVerified = true
 * при любом входе, если в Auth у пользователя emailVerified === true.
 */
export const updateVerifiedOnSignIn = beforeUserSignedIn(async (event) => {
  const data = event.data; // тип: AuthUserRecord | undefined
  if (!data) {
    logger.warn("beforeUserSignedIn: no event.data");
    return {};
  }

  const {uid, emailVerified} = data; // теперь ок: data точно не undefined

  if (emailVerified) {
    await getDatabase().ref(`users/${uid}/emailVerified`).set(true);
    logger.info("emailVerified set to true in RTDB", {uid});
  } else {
    logger.info("emailVerified is false; RTDB not updated", {uid});
  }

  return {}; // ничего в кредах не меняем
});
