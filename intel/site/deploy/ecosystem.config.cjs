module.exports = {
  apps: [
    {
      name: "evedeck-intel",
      cwd: __dirname + "/..",
      script: "node_modules/.bin/next",
      args: "start -p 3007 -H 127.0.0.1",
      interpreter: "node",
      env: { NODE_ENV: "production" }
    }
  ]
};
