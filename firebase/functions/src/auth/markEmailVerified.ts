// functions/src/auth/markEmailVerified.ts
import {onCall, HttpsError} from "firebase-functions/v2/https";
import {getAuth} from "firebase-admin/auth";
import {getDatabase} from "firebase-admin/database";
import {getApps, initializeApp} from "firebase-admin/app";

// Безопасная инициализация Admin SDK (один раз)
if (getApps().length === 0) {
  initializeApp();
}

/**
 * Callable-функция.
 * Проверяет по Admin SDK, что email у текущего пользователя подтверждён,
 * и если да — пишет в RTDB: users/{uid}/emailVerified = true
 */
export const markEmailVerified = onCall(async (req) => {
  const uid = req.auth?.uid;
  if (!uid) {
    throw new HttpsError("unauthenticated", "Sign-in required.");
  }

  // Получаем «истину» с сервера (а не из клиента)
  const user = await getAuth().getUser(uid);

  if (!user.email) {
    throw new HttpsError("failed-precondition", "No email on account.");
  }
  if (!user.emailVerified) {
    throw new HttpsError("failed-precondition", "Email not verified yet.");
  }

  await getDatabase().ref(`users/${uid}/emailVerified`).set(true);
  return {ok: true};
});
