import {onUserDeleted} from "firebase-functions/v2/identity";
import {initializeApp} from "firebase-admin/app";
import {getDatabase} from "firebase-admin/database";
import * as logger from "firebase-functions/logger";

initializeApp();

export const cleanupUserData = onUserDeleted(async (event) => {
  const uid = event.data.uid;
  const db = getDatabase();
  try {
    await db.ref(`users/${uid}`).remove();
    logger.info(`Removed RTDB node users/${uid}`);
  } catch (e) {
    logger.error(`Failed to remove users/${uid}`, e as Error);
  }
});


