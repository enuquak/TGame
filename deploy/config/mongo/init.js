for (const [database, username, password] of [
  ["tgame_dev", "tgame_dev", process.env.MONGO_DEV_PASSWORD],
  ["tgame_test", "tgame_test", process.env.MONGO_TEST_PASSWORD],
]) {
  db.getSiblingDB(database).createUser({
    user: username,
    pwd: password,
    roles: [{ role: "readWrite", db: database }],
  });
}
